using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.Networking;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons
{
    internal sealed record StreamJob(Guid SceneId, int TimelineIndex, int Frame, bool Keyframe);

    internal sealed class PreviewStream : IDisposable
    {
        private const int MaxWidth = 1280;
        private const int MinWidth = 320;
        private const int MaxFps = 30;
        private const int StillRefinements = 3;
        private const int StillRefineMs = 120;
        private const int PayloadType = 102;
        private const int MaxMessageSize = 256 * 1024;
        private const double MaxLeadSeconds = 2;

        private readonly Lock gate = new();
        private readonly RtcConnection connection;
        private readonly RtcTrack? track;
        private readonly RtcChannel? control;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DisposeCollector disposer = new();
        private H264Encoder? encoder;
        private bool encoderFailed;

        private Guid sceneId;
        private int timelineIndex = -1;
        private double anchorFrame;
        private long anchorAt;
        private bool playing;
        private double speed = 1;
        private double leadSeconds;
        private int sceneFps = 30;
        private int requestedWidth = 960;
        private int lastFrame = -1;
        private int stillPending;
        private bool needKeyframe = true;
        private long nextDue;


        public Guid Viewer { get; }
        public Guid SessionId { get; }
        public bool IsDead { get; private set; }

        public event Action? Changed;

        public PreviewStream(Guid viewer, Guid sessionId, IReadOnlyList<string> iceServers, Func<RtcSignalEvent, Task> sendSignal)
        {
            Viewer = viewer;
            SessionId = sessionId;
            connection = new RtcConnection(iceServers, MaxMessageSize);
            connection.LocalDescription += (sdp, type) => _ = sendSignal(new RtcSignalEvent(sessionId, type, sdp) { DateTime = DateTime.UtcNow });
            connection.LocalCandidate += (candidate, mid) => _ = sendSignal(new RtcSignalEvent(sessionId, "candidate", candidate, mid) { DateTime = DateTime.UtcNow });
            connection.StateChanged += state =>
            {
                if (state is RtcNative.StateFailed or RtcNative.StateClosed or RtcNative.StateDisconnected)
                {
                    IsDead = true;
                    Changed?.Invoke();
                }
            };

            track = connection.AddH264Track("video", (uint)Random.Shared.Next(1, int.MaxValue), PayloadType);
            if (track != null)
            {
                track.Open += () => RequestKeyframe();
                track.KeyframeRequested += RequestKeyframe;
            }

            control = connection.CreateChannel("control");
            if (control != null)
            {
                control.Text += OnControl;
                control.Close += () =>
                {
                    IsDead = true;
                    Changed?.Invoke();
                };
            }
            disposer.Collect(connection);
            disposer.Collect(track);
            disposer.Collect(control);
            disposer.CollectAction(nameof(encoder), () =>
            {
                lock (gate)
                {
                    encoder?.Dispose();
                    encoder = null;
                }
            });
        }

        public void ApplyRemote(RtcSignalEvent signal)
        {
            switch (signal.Kind)
            {
                case "answer":
                    connection.SetRemoteDescription(signal.Data, "answer");
                    break;
                case "candidate":
                    connection.AddRemoteCandidate(signal.Data, signal.Mid);
                    break;
            }
        }

        private void RequestKeyframe()
        {
            lock (gate)
            {
                needKeyframe = true;
                if (!playing) stillPending = Math.Max(stillPending, 1);
                nextDue = 0;
            }
            Changed?.Invoke();
        }

        private void OnControl(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (!root.TryGetProperty("t", out var type)) return;
                if (type.GetString() == "lead")
                {
                    if (root.TryGetProperty("lead", out var value) && value.TryGetDouble(out var seconds))
                    {
                        lock (gate) leadSeconds = Math.Clamp(seconds, 0, MaxLeadSeconds);
                    }
                    return;
                }
                if (type.GetString() != "state") return;

                lock (gate)
                {
                    if (root.TryGetProperty("lead", out var stateLead) && stateLead.TryGetDouble(out var leadValue)) leadSeconds = Math.Clamp(leadValue, 0, MaxLeadSeconds);
                    var newScene = root.TryGetProperty("scene", out var s) && Guid.TryParse(s.GetString(), out var parsed) ? parsed : Guid.Empty;
                    var newIndex = root.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    var frame = root.TryGetProperty("frame", out var f) ? f.GetDouble() : 0;
                    var isPlaying = root.TryGetProperty("playing", out var p) && p.GetBoolean();
                    var newSpeed = root.TryGetProperty("speed", out var sp) ? Math.Clamp(sp.GetDouble(), 0.1, 4) : 1;
                    var fps = root.TryGetProperty("fps", out var fp) ? Math.Clamp(fp.GetInt32(), 1, 240) : 30;
                    var width = root.TryGetProperty("width", out var w) ? w.GetInt32() : requestedWidth;

                    var sceneChanged = newScene != sceneId || newIndex != timelineIndex;
                    if (sceneChanged) needKeyframe = true;
                    sceneId = newScene;
                    timelineIndex = newIndex;
                    sceneFps = fps;
                    speed = newSpeed;
                    anchorFrame = Math.Max(0, frame);
                    anchorAt = clock.ElapsedMilliseconds;
                    playing = isPlaying;
                    var clamped = Math.Clamp(width, MinWidth, MaxWidth);
                    if (Math.Abs(clamped - requestedWidth) > 64)
                    {
                        requestedWidth = clamped;
                        ResetEncoder();
                    }
                    if (!playing)
                    {
                        stillPending = StillRefinements;
                        lastFrame = -1;
                    }
                    nextDue = 0;
                }
            }
            catch
            {
            }
            Changed?.Invoke();
        }

        private void ResetEncoder()
        {
            encoder?.Dispose();
            encoder = null;
            encoderFailed = false;
            needKeyframe = true;
        }

        public long NextDue
        {
            get
            {
                lock (gate)
                {
                    if (IsDead || track?.IsOpen != true || timelineIndex < 0) return long.MaxValue;
                    if (!playing && stillPending <= 0) return long.MaxValue;
                    return nextDue;
                }
            }
        }

        public long Now => clock.ElapsedMilliseconds;

        public StreamJob? TakeDue(int minIntervalMs)
        {
            lock (gate)
            {
                if (IsDead || encoderFailed || track?.IsOpen != true || timelineIndex < 0) return null;
                var now = clock.ElapsedMilliseconds;
                if (now < nextDue) return null;

                if (playing)
                {
                    var frame = (int)(anchorFrame + ((now - anchorAt) / 1000.0 + leadSeconds) * sceneFps * speed);
                    var interval = Math.Max(minIntervalMs, 1000 / Math.Min(MaxFps, Math.Max(1, sceneFps)));
                    nextDue = now + interval;
                    if (frame == lastFrame && !needKeyframe) return null;
                    lastFrame = frame;
                    return new StreamJob(sceneId, timelineIndex, frame, needKeyframe);
                }

                if (stillPending <= 0) return null;
                stillPending--;
                nextDue = now + StillRefineMs;
                return new StreamJob(sceneId, timelineIndex, (int)anchorFrame, needKeyframe);
            }
        }

        public unsafe void Deliver(BitmapSource bitmap, StreamJob job)
        {
            if (IsDead || track == null) return;

            lock (gate)
            {
                if (encoder == null && !encoderFailed)
                {
                    var width = Math.Min(requestedWidth, bitmap.PixelWidth) & ~1;
                    var height = (int)Math.Round(width * bitmap.PixelHeight / (double)bitmap.PixelWidth) & ~1;
                    var bitrate = Math.Clamp((long)(width * (long)height * MaxFps * 0.09), 600_000L, 4_000_000L);
                    encoder = H264Encoder.Create(width, height, MaxFps, bitrate);
                    if (encoder == null)
                    {
                        encoderFailed = true;
                        control?.Send("{\"t\":\"unsupported\"}");
                        return;
                    }
                    needKeyframe = true;
                    control?.Send($"{{\"t\":\"encoder\",\"name\":\"{encoder.Name}\",\"width\":{width},\"height\":{height}}}");
                }
            }

            var current = encoder;
            if (current == null) return;

            var stride = bitmap.PixelWidth * 4;
            var size = stride * bitmap.PixelHeight;
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                bitmap.CopyPixels(buffer, stride, 0);
                byte[]? encoded;
                bool keyframe;
                lock (gate)
                {
                    keyframe = needKeyframe || job.Keyframe;
                    fixed (byte* pixels = buffer) encoded = current.Encode(pixels, stride, bitmap.PixelWidth, bitmap.PixelHeight, keyframe);
                    if (encoded is { Length: > 0 }) needKeyframe = false;
                }
                if (encoded is { Length: > 0 } && track.SendFrame(encoded, clock.Elapsed.TotalSeconds, out var timestamp))
                {
                    control?.Send($"{{\"t\":\"f\",\"ts\":{timestamp},\"frame\":{job.Frame}}}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            IsDead = true;
            Changed = null;
            Task.Run(disposer.Dispose);
        }
    }

    internal sealed class PreviewStreams(PreviewService service, IReadOnlyList<string> iceServers, Func<Guid, RtcSignalEvent, Task> sendSignal) : IDisposable
    {
        private const int MaxStreams = 8;

        private readonly ConcurrentDictionary<Guid, PreviewStream> streams = new();
        private bool disposed;

        public static bool IsAvailable => PeerTransport.IsAvailable && H264Encoder.IsAvailable;

        public void HandleSignal(Guid from, RtcSignalEvent signal)
        {
            if (disposed) return;
            try
            {
                switch (signal.Kind)
                {
                    case "stream-open":
                        Open(from, signal.SessionId);
                        break;
                    case "stream-close":
                        Close(signal.SessionId);
                        break;
                    default:
                        if (streams.TryGetValue(signal.SessionId, out var stream) && stream.Viewer == from) stream.ApplyRemote(signal);
                        break;
                }
            }
            catch
            {
            }
        }

        private void Open(Guid viewer, Guid sessionId)
        {
            foreach (var old in streams.Values.Where(s => s.Viewer == viewer).ToList()) Close(old.SessionId);

            if (!IsAvailable || streams.Count >= MaxStreams)
            {
                _ = sendSignal(viewer, new RtcSignalEvent(sessionId, "stream-unavailable", string.Empty) { DateTime = DateTime.UtcNow });
                return;
            }

            var stream = new PreviewStream(viewer, sessionId, iceServers, signal => sendSignal(viewer, signal));
            streams[sessionId] = stream;
            service.AddStream(stream);
        }

        private void Close(Guid sessionId)
        {
            if (!streams.TryRemove(sessionId, out var stream)) return;
            service.RemoveStream(stream);
            stream.Dispose();
        }


        public void Forget(Guid viewer)
        {
            foreach (var stream in streams.Values.Where(s => s.Viewer == viewer).ToList()) Close(stream.SessionId);
        }

        public void Dispose()
        {
            disposed = true;
            foreach (var id in streams.Keys.ToList()) Close(id);
        }
    }
}
