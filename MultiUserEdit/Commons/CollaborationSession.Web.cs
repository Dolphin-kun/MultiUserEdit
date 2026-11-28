using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using System.IO;
using System.Windows;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        private static readonly Lazy<bool> canStream = new(() => PreviewStreams.IsAvailable);

        private void PublishRenderStatus()
        {
            try
            {
                if (disposed || !IsConnected || webParticipants.IsEmpty) return;

                var scenes = Application.Current?.Dispatcher.Invoke(DescribeSceneReadiness) ?? [];
                var viewers = previewService.ActiveViewers;
                var renderMs = Math.Round(previewService.AverageRenderMs, 1);
                var interval = previewService.SuggestedIntervalMs;
                var signature = $"{viewers}|{Math.Round(renderMs / 10)}|{interval / 50}|{string.Join(";", scenes.Select(s => $"{s.Index}:{s.MissingItems}:{s.MissingMedia}"))}";
                var now = Environment.TickCount64;
                if (signature == lastRenderStatus && now - lastRenderStatusAt < RenderStatusHeartbeatMs) return;
                lastRenderStatus = signature;
                lastRenderStatusAt = now;

                var status = new RenderStatusEvent(true, viewers, renderMs, interval, scenes)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId,
                    CanStream = canStream.Value
                };
                _ = sessionClient.SendToManyAsync([.. webParticipants.Keys.Select(id => id.ToString())], status);
            }
            catch
            {
            }
        }

        private IReadOnlyList<SceneReadiness> DescribeSceneReadiness()
        {
            var timelines = Scenes?.Timelines;
            if (timelines == null) return [];

            var result = new List<SceneReadiness>();
            for (var index = 0; index < timelines.Count; index++)
            {
                var timeline = timelines[index];
                var missingMedia = 0;
                foreach (var item in timeline.Items)
                {
                    var path = item switch
                    {
                        VideoItem video => video.FilePath,
                        AudioItem audio => audio.FilePath,
                        ImageItem image => image.FilePath,
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(path) && !File.Exists(path)) missingMedia++;
                }
                result.Add(new SceneReadiness(index, timeline.ID, RenderReadiness.MissingIn(index), missingMedia));
            }
            return result;
        }

        private RoomKind selectedRoomKind;

        private Task SendPreviewFrameAsync(Guid requester, PreviewFrameEvent frame)
        {
            var stamped = frame with { ExecutorId = LocalUserId };
            var target = requester.ToString();
            var viaFileChannel = sessionClient.HasFileChannel
                && peerFileChannels.TryGetValue(requester, out var hasChannel) && hasChannel;

            return viaFileChannel
                ? sessionClient.SendFileDataToManyAsync([target], stamped)
                : sessionClient.SendAsync(target, stamped);
        }

        private static readonly TimeSpan FileWantRetry = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan FileWantBatchDelay = TimeSpan.FromSeconds(3);
        private const int MaxFilesPerRequest = 200;

        private readonly Dictionary<string, DateTime> wantedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> pendingWants = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> pulledFiles = new(StringComparer.OrdinalIgnoreCase);
        private bool wantFlushScheduled;

        private string? ResolveServablePath(string name, ref Dictionary<string, string>? projectFiles)
        {
            if (string.IsNullOrWhiteSpace(name) || !Settings.MultiUserEditSettings.Default.IsExtensionAllowed(name)) return null;

            var path = MediaFileResolver.ResolveLocalOriginal(name);
            if (path == null)
            {
                var received = MediaFileResolver.ResolveLocalTempPath(name);
                if (File.Exists(received)) path = received;
            }

            if (path == null)
            {
                projectFiles ??= IndexProjectFiles();
                if (projectFiles.TryGetValue(name, out var found) && File.Exists(found)) path = found;
            }

            if (path == null || fileTransferManager.IsSendDenied(path) || DescribeFileSendBlock(path) != null) return null;

            return path;
        }

        private string? ResolveMediaForWeb(string name)
        {
            Dictionary<string, string>? projectFiles = null;
            return ResolveServablePath(name, ref projectFiles);
        }

        private Task<bool> ConfirmMediaForWebAsync(string path) =>
            MediaFileResolver.IsReceivedCopy(path) ? Task.FromResult(true) : fileTransferManager.ConfirmSendAsync(path);

        private void SendMediaToWeb(Guid target, string path, string name) =>
            _ = fileTransferManager.SendDirectAsync(new SharedFile(path, name), target, sessionClient, LocalUserId);

        private Dictionary<string, string> IndexProjectFiles()
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Scenes == null) return index;

            foreach (var timeline in Scenes.Timelines)
            {
                foreach (var item in timeline.Items)
                {
                    try
                    {
                        var (_, files) = MediaFileResolver.SerializeForSync(item);
                        foreach (var file in files) index.TryAdd(file.Name, file.FullPath);
                    }
                    catch
                    {
                    }
                }
            }

            return index;
        }

        private void RememberLocalOriginals()
        {
            if (Scenes == null) return;

            foreach (var timeline in Scenes.Timelines)
            {
                foreach (var item in timeline.Items)
                {
                    try
                    {
                        MediaFileResolver.SerializeForSync(item);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private bool keptRoomJoined;
        private bool snapshotUploadPending;
        private long lastAppliedSeq;

        private static readonly TimeSpan SnapshotQuietPeriod = TimeSpan.FromSeconds(1);
    }
}
