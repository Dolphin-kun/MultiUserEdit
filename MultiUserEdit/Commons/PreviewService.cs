using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiUserEdit.Commons.Events;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;

namespace MultiUserEdit.Commons
{
    internal sealed class PreviewService : IDisposable
    {
        private const int MaxWidth = 1920;
        private const int MinWidth = 160;
        private const int JpegQuality = 85;
        private const int MaxCachedSources = 4;
        private const double RestShare = 1.0;
        private const int MaxRestMs = 250;
        private const int ViewerWindowMs = 5000;
        private const int MinIntervalMs = 125;
        private const int MaxIntervalMs = 2000;
        private const int MaxWaitMs = 1000;

        private sealed record CachedSource(Timeline Timeline, int Width, int Height, ITimelineSourceAndDevices Source);

        private readonly Func<Scenes?> getScenes;
        private readonly Func<Guid, bool> isParticipant;
        private readonly Func<Guid, PreviewFrameEvent, Task> sendFrame;

        private readonly Dictionary<Guid, PreviewRequestEvent> latest = [];
        private readonly ConcurrentDictionary<Guid, long> lastRequestAt = new();
        private readonly ConcurrentDictionary<PreviewStream, byte> streams = new();
        private readonly SemaphoreSlim signal = new(0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<CachedSource> sources = [];
        private double averageRenderMs = 40;

        public PreviewService(Func<Scenes?> getScenes, Func<Guid, bool> isParticipant, Func<Guid, PreviewFrameEvent, Task> sendFrame)
        {
            this.getScenes = getScenes;
            this.isParticipant = isParticipant;
            this.sendFrame = sendFrame;
            _ = Task.Run(LoopAsync);
        }

        public int ActiveViewers
        {
            get
            {
                var now = Environment.TickCount64;
                foreach (var pair in lastRequestAt)
                {
                    if (now - pair.Value > ViewerWindowMs * 6) lastRequestAt.TryRemove(pair);
                }
                return lastRequestAt.Count(pair => now - pair.Value <= ViewerWindowMs);
            }
        }

        public double AverageRenderMs => Volatile.Read(ref averageRenderMs);

        public int SuggestedIntervalMs =>
            (int)Math.Clamp(AverageRenderMs * (1 + RestShare) * Math.Max(1, ActiveViewers), MinIntervalMs, MaxIntervalMs);

        public void Request(PreviewRequestEvent request)
        {
            if (stop.IsCancellationRequested) return;

            lastRequestAt[request.ExecutorId] = Environment.TickCount64;
            lock (latest) latest[request.ExecutorId] = request;
            signal.Release();
        }

        public void AddStream(PreviewStream stream)
        {
            streams[stream] = 0;
            stream.Changed += Wake;
            Wake();
        }

        public void RemoveStream(PreviewStream stream)
        {
            stream.Changed -= Wake;
            streams.TryRemove(stream, out _);
        }

        private void Wake()
        {
            if (signal.CurrentCount == 0) signal.Release();
        }

        private async Task LoopAsync()
        {
            try
            {
                await ServeAsync();
            }
            finally
            {
                lock (sources)
                {
                    foreach (var cached in sources) cached.Source.Dispose();
                    sources.Clear();
                }
            }
        }

        private int WaitMs()
        {
            var wait = MaxWaitMs;
            foreach (var stream in streams.Keys)
            {
                var due = stream.NextDue;
                if (due == long.MaxValue) continue;
                wait = (int)Math.Clamp(due - stream.Now, 0, wait);
            }
            return wait;
        }

        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await signal.WaitAsync(WaitMs(), stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                foreach (var dead in streams.Keys.Where(s => s.IsDead).ToList()) RemoveStream(dead);

                KeyValuePair<Guid, PreviewRequestEvent>[] images;
                lock (latest)
                {
                    images = [.. latest];
                    latest.Clear();
                }

                var streamInterval = SuggestedIntervalMs > MinIntervalMs ? SuggestedIntervalMs / 2 : 0;
                var streamJobs = new List<(PreviewStream Stream, StreamJob Job)>();
                foreach (var stream in streams.Keys)
                {
                    if (stream.TakeDue(streamInterval) is { } job)
                    {
                        streamJobs.Add((stream, job));
                        lastRequestAt[stream.Viewer] = Environment.TickCount64;
                    }
                }

                var keys = images
                    .Where(pair => isParticipant(pair.Key))
                    .Select(pair => (pair.Value.SceneId, pair.Value.TimelineIndex, pair.Value.Frame))
                    .Concat(streamJobs.Select(s => (s.Job.SceneId, s.Job.TimelineIndex, s.Job.Frame)))
                    .Distinct()
                    .ToList();

                foreach (var (sceneId, timelineIndex, frame) in keys)
                {
                    if (stop.IsCancellationRequested) return;

                    var watch = Stopwatch.StartNew();
                    try
                    {
                        var bitmap = RenderBitmap(sceneId, timelineIndex, frame);
                        if (bitmap != null)
                        {
                            var forKey = images.Where(pair => isParticipant(pair.Key)
                                && pair.Value.SceneId == sceneId && pair.Value.TimelineIndex == timelineIndex && pair.Value.Frame == frame);
                            foreach (var group in forKey.GroupBy(pair => (Width: Math.Clamp(pair.Value.Width, MinWidth, MaxWidth), pair.Value.Lossless)))
                            {
                                var encoded = EncodeImage(bitmap, group.First().Value);
                                foreach (var (requester, request) in group)
                                    await sendFrame(requester, encoded with { RequestId = request.RequestId });
                            }

                            foreach (var (stream, job) in streamJobs.Where(s => s.Job.SceneId == sceneId && s.Job.TimelineIndex == timelineIndex && s.Job.Frame == frame))
                                stream.Deliver(bitmap, job);
                        }
                    }
                    catch
                    {
                    }

                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    Volatile.Write(ref averageRenderMs, averageRenderMs * 0.8 + elapsed * 0.2);
                    var rest = (int)Math.Min(MaxRestMs, elapsed * RestShare);
                    if (rest > 0)
                    {
                        try
                        {
                            await Task.Delay(rest, stop.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
        }

        private BitmapSource? RenderBitmap(Guid sceneId, int timelineIndex, int frameIndex)
        {
            var cached = Application.Current?.Dispatcher.Invoke(() => FindSource(sceneId, timelineIndex));
            if (cached == null) return null;

            var fps = Math.Max(1, cached.Timeline.VideoInfo.FPS);
            cached.Source.Update(TimeSpan.FromSeconds(Math.Max(0, frameIndex) / (double)fps), TimelineSourceUsage.Paused);

            var bitmap = cached.Source.RenderBitmapSource();
            bitmap.Freeze();
            return bitmap;
        }

        private static PreviewFrameEvent EncodeImage(BitmapSource bitmap, PreviewRequestEvent request)
        {
            var width = Math.Clamp(request.Width, MinWidth, MaxWidth);
            BitmapSource image = bitmap;
            if (width < bitmap.PixelWidth)
            {
                var scale = width / (double)bitmap.PixelWidth;
                image = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
                image.Freeze();
            }

            BitmapEncoder encoder = request.Lossless ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);

            return new PreviewFrameEvent(request.RequestId, request.SceneId, request.Frame,
                request.Lossless ? "png" : "jpeg", Convert.ToBase64String(stream.ToArray()), image.PixelWidth, image.PixelHeight)
            {
                DateTime = DateTime.UtcNow
            };
        }

        private CachedSource? FindSource(Guid sceneId, int timelineIndex)
        {
            var scenes = getScenes();
            if (scenes == null) return null;

            var timeline = scenes.Timelines.FirstOrDefault(t => t.ID == sceneId)
                ?? scenes.Timelines.ElementAtOrDefault(timelineIndex);
            if (timeline == null) return null;

            var width = timeline.VideoInfo.Width;
            var height = timeline.VideoInfo.Height;

            lock (sources)
            {
                var existing = sources.FirstOrDefault(s => ReferenceEquals(s.Timeline, timeline));
                if (existing != null && existing.Width == width && existing.Height == height)
                {
                    sources.Remove(existing);
                    sources.Add(existing);
                    return existing;
                }

                if (existing != null)
                {
                    sources.Remove(existing);
                    existing.Source.Dispose();
                }

                var scene = new Scene(timeline, scenes, []);
                if (!scene.TryCreateVideoSource(out ITimelineSourceAndDevices source)) return null;

                var created = new CachedSource(timeline, width, height, source);
                sources.Add(created);

                while (sources.Count > MaxCachedSources)
                {
                    sources[0].Source.Dispose();
                    sources.RemoveAt(0);
                }

                return created;
            }
        }

        public void Dispose()
        {
            if (!stop.IsCancellationRequested) stop.Cancel();
        }
    }
}
