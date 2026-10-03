using MultiUserEdit.Commons.Events;
using MultiUserEdit.Networking;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;

namespace MultiUserEdit.Commons
{
    internal sealed class RoomMigration(Func<SessionClient> getSessionClient, Func<Guid> getLocalUserId)
    {
        public const string PhasePrepare = "prepare";
        public const string PhaseCommit = "commit";
        public const string PhaseAbort = "abort";

        private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(20);

        private DateTime quietSince = DateTime.UtcNow;
        private bool running;

        public bool IsRunning => running;

        public string? PendingRoomId { get; private set; }
        public string? PendingHostKey { get; private set; }

        private readonly HashSet<Guid> acked = [];
        private readonly HashSet<Guid> failed = [];
        private DateTime startedAt;

        public void NoteParticipantCount(int count)
        {
            if (count > SessionClient.SmallRoomLimit) quietSince = DateTime.UtcNow;
        }

        public bool ShouldStart(int participantCount) =>
            !running
            && participantCount > 0
            && participantCount <= SessionClient.SmallRoomLimit
            && DateTime.UtcNow - quietSince >= QuietPeriod;

        public (string RoomId, string HostKey) Begin()
        {
            running = true;
            startedAt = DateTime.UtcNow;
            acked.Clear();
            failed.Clear();

            PendingRoomId = Guid.NewGuid().ToString();
            PendingHostKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            return (PendingRoomId, PendingHostKey);
        }

        public void NoteAck(Guid userId, bool success)
        {
            if (!running) return;

            if (success) acked.Add(userId);
            else failed.Add(userId);
        }

        public bool HasFailure => failed.Count > 0;

        public bool AllAcked(IEnumerable<Guid> expected) => expected.All(acked.Contains);

        public bool TimedOut => DateTime.UtcNow - startedAt > AckTimeout;

        public void Finish()
        {
            running = false;
            quietSince = DateTime.UtcNow;
            PendingRoomId = null;
            PendingHostKey = null;
            acked.Clear();
            failed.Clear();
        }

        public void Broadcast(string phase, string roomId)
        {
            var evt = new RoomMigrationEvent(roomId, phase)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = getLocalUserId()
            };

            try
            {
                _ = getSessionClient().SendAsync(null, evt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] migration broadcast failed: {ex.Message}");
            }
        }

        public void SendAck(Guid hostId, bool success)
        {
            var evt = new RoomMigrationAckEvent(success)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = getLocalUserId()
            };

            try
            {
                _ = getSessionClient().SendAsync(hostId.ToString(), evt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] migration ack failed: {ex.Message}");
            }
        }

        public static void NotifyMoved() =>
            Application.Current?.Dispatcher.InvokeAsync(() => MessageBox.Show(
                "人数が少ない状態が続いたため、小さい部屋へ移動しました。\n"
                + "ルームIDが変わっているので、新しく招待する場合は招待リンクを取り直してください。",
                "部屋を移動しました",
                MessageBoxButton.OK));
    }
}
