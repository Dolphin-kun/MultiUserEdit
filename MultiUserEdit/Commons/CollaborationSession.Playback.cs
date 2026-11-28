using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using System.Windows;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        internal void HandleCursorMovedEvent(CursorMovedEvent evt)
        {
            if (evt.ExecutorId == Guid.Empty || evt.ExecutorId == LocalUserId) return;
            var p = EnsureParticipant(evt.ExecutorId);
            p.CurrentFrame = evt.CurrentFrame;
            p.CurrentTimelineIndex = evt.TimelineIndex;
            p.IsPlaying = evt.IsPlaying;
            p.FrameUpdatedAt = DateTime.UtcNow;

            if (!IsHost && CurrentUserPermission.CanSyncSeekPosition && p.Role == UserRole.Host)
            {
                QueuePlaybackSync(evt.CurrentFrame, evt.IsPlaying);
            }
        }

        private static readonly TimeSpan SeekApplyInterval = TimeSpan.FromMilliseconds(120);

        private readonly System.Threading.Lock seekLock = new();
        private (int Frame, bool IsPlaying)? pendingSeek;
        private DateTime lastSeekAppliedAt = DateTime.MinValue;
        private bool seekFlushScheduled;

        private void QueuePlaybackSync(int frame, bool isPlaying)
        {
            lock (seekLock)
            {
                pendingSeek = (frame, isPlaying);

                var now = DateTime.UtcNow;
                var waited = now - lastSeekAppliedAt;

                if (waited >= SeekApplyInterval)
                {
                    lastSeekAppliedAt = now;
                    pendingSeek = null;
                    _ = SyncPlaybackStateAsync(frame, isPlaying);
                    return;
                }

                if (seekFlushScheduled) return;
                seekFlushScheduled = true;

                var delay = SeekApplyInterval - waited;
                _ = Task.Delay(delay).ContinueWith(
                    _ => Application.Current?.Dispatcher.InvokeAsync(FlushPendingSeek),
                    TaskScheduler.Default);
            }
        }

        private void FlushPendingSeek()
        {
            (int Frame, bool IsPlaying) target;
            lock (seekLock)
            {
                seekFlushScheduled = false;
                if (pendingSeek == null) return;

                target = pendingSeek.Value;
                pendingSeek = null;
                lastSeekAppliedAt = DateTime.UtcNow;
            }

            _ = SyncPlaybackStateAsync(target.Frame, target.IsPlaying);
        }

        private int lastAppliedRemoteFrame = -1;
        private DateTime lastAppliedRemoteFrameAt = DateTime.MinValue;

        private async Task SyncPlaybackStateAsync(int frame, bool isPlaying)
        {
            lastAppliedRemoteFrame = frame;
            lastAppliedRemoteFrameAt = DateTime.UtcNow;

            if (!isPlaying && FirstOrDefaultTimeline != null)
            {
                var previous = isApplyingRemoteEvent;
                isApplyingRemoteEvent = true;
                try
                {
                    FirstOrDefaultTimeline.UnlockCurrentFrame();
                    FirstOrDefaultTimeline.CurrentFrame = frame;
                }
                finally
                {
                    isApplyingRemoteEvent = previous;
                }
            }

            if (previewSeekAction != null)
            {
                await previewSeekAction(frame, isPlaying);
            }
        }
    }
}
