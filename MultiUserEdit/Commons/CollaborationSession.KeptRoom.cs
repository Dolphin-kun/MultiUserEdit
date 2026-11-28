using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using System.Windows;
using YukkuriMovieMaker.Project;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        private void HandleKeptRoomJoined(bool hasSnapshot)
        {
            if (!IsConnected && !isJoining) return;

            if (keptRoomJoined)
            {
                _ = sessionClient.SendAsync("server", new { type = "system:catchup", since = lastAppliedSeq });
                return;
            }

            keptRoomJoined = true;

            if (localUserRole != UserRole.Host) return;

            if (hasSnapshot) RequestServerSync();
            else RequestSnapshotUpload();
        }

        private void RequestServerSync()
        {
            if (!sessionClient.IsConnected) return;

            awaitingInitialSync = true;
            _ = sessionClient.SendAsync(null, new SyncRequestEvent
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
        }

        private void RequestSnapshotUpload()
        {
            snapshotUploadPending = true;
            TryUploadSnapshot();
        }

        private bool IsReadyForSnapshot()
        {
            if (isApplyingSyncScenes || awaitingInitialSync || syncScenesTransfer.IsHolding) return false;
            if (sessionClient.HasUnacknowledgedEdits) return false;
            if (DateTime.Now - sessionClient.LastProjectEditAt < SnapshotQuietPeriod) return false;

            lock (receivedEvents) return receivedEvents.Count == 0;
        }

        private void TryUploadSnapshot()
        {
            if (!snapshotUploadPending) return;
            if (!IsKeptRoom || !sessionClient.IsConnected)
            {
                snapshotUploadPending = false;
                return;
            }

            if (!IsReadyForSnapshot()) return;

            snapshotUploadPending = false;
            SendSyncScenes("server", isManual: false, baseSeq: lastAppliedSeq, transferFiles: false);
        }

        private void AskWhatToKeepAfterHostLeft(string reason)
        {
            if (hostLeftHandled) return;
            hostLeftHandled = true;

            var keep = MessageBox.Show(
                string.Format(Texts.KeepTimelineQuestion, reason),
                Texts.RoomEndedTitle,
                MessageBoxButton.YesNo) == MessageBoxResult.Yes;

            if (!keep) ClearAllTimelines();

            _ = StopNetworkAsync();
        }

        private void ClearAllTimelines()
        {
            if (Scenes == null) return;

            var previous = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            try
            {
                using var undoScope = UndoRecordSuppressor.Suppress(undoRedoManager);
                foreach (var timeline in Scenes.Timelines)
                {
                    var items = timeline.Items.ToArray();
                    if (items.Length > 0) timeline.DeleteItems(items);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not clear the timeline while syncing", ex);
            }
            finally
            {
                isApplyingRemoteEvent = previous;
            }
        }
    }
}
