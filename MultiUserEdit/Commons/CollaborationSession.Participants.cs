using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using System.Windows;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        private void UpdateParticipantActivity(Guid executorId)
        {
            if (executorId == Guid.Empty || executorId == LocalUserId || executorId == SessionClient.ServerUserId) return;

            NotePeerAlive(executorId);

            var p = EnsureParticipant(executorId);
            p.Status = UserStatus.Active;
            p.LastActivity = DateTime.Now;
        }

        private Participant EnsureParticipant(Guid userId)
        {
            var p = Participants.FirstOrDefault(x => x.UserId == userId);
            if (p != null)
            {
                p.LastActivity = DateTime.Now;
                p.Status = UserStatus.Active;
                return p;
            }

            p = new Participant
            {
                UserId = userId,
                UserName = $"User {userId.ToString()[..8]}",
                Role = UserRole.Guest,
                LastActivity = DateTime.Now,
                JoinedAt = DateTime.Now,
                ThemeColor = ParticipantColorGenerator.Generate(userId),
                Status = UserStatus.Active
            };

            AddParticipantSorted(p);
            return p;
        }

        internal void HandlePresenceEvent(PresenceEvent evt)
        {
            if (evt.UserId != evt.ExecutorId) return;

            var role = NormalizeRole(evt.UserId, evt.Role);

            if (evt.UserId != LocalUserId) peerFileChannels[evt.UserId] = evt.HasFileChannel;
            if (evt.UserId != LocalUserId)
            {
                if (evt.CanDirect && !evt.IsWeb) directPeers[evt.UserId] = 0;
                else directPeers.TryRemove(evt.UserId, out _);
            }
            if (evt.IsWeb) webParticipants[evt.UserId] = 0;
            if (evt.UserId != LocalUserId)
            {
                if (evt.CanCatchUp) catchUpPeers[evt.UserId] = 0;
                else catchUpPeers.TryRemove(evt.UserId, out _);
            }

            var p = Participants.FirstOrDefault(x => x.UserId == evt.UserId);
            if (p == null)
            {
                p = new Participant
                {
                    UserId = evt.UserId,
                    ProfileId = evt.ProfileId,
                    UserName = evt.UserName,
                    Description = ProfileText.NormalizeDescription(evt.Description),
                    Role = role,
                    HasFileChannel = evt.HasFileChannel,
                    IsWeb = evt.IsWeb,
                    LastActivity = DateTime.Now,
                    JoinedAt = DateTime.Now,
                    ThemeColor = ParticipantColorGenerator.Generate(evt.UserId),
                    Status = UserStatus.Active
                };
                AddParticipantSorted(p);
            }
            else
            {
                p.UserName = evt.UserName;
                p.Description = ProfileText.NormalizeDescription(evt.Description);
                p.Role = role;
                p.HasFileChannel = evt.HasFileChannel;
                p.LastActivity = DateTime.Now;
                p.Status = UserStatus.Active;
                if (evt.ProfileId != Guid.Empty) p.ProfileId = evt.ProfileId;
                MoveHostToTopIfNeeded(p);
            }

            if (!IsHost && role == UserRole.Host && !IsCompatibleVersion(evt.Version))
            {
                HandleHostVersionMismatch(evt.Version);
                return;
            }

            if (IsHost && !evt.IsReply)
            {
                var permissionEvt = new PermissionUpdatedEvent(null, GlobalDefaultPermission)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                _ = sessionClient.SendAsync(evt.UserId.ToString(), permissionEvt);

                foreach (var (userId, permission) in permissionOverrides)
                {
                    _ = sessionClient.SendAsync(evt.UserId.ToString(), new PermissionUpdatedEvent(userId, permission)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    });
                }
            }

            if (!evt.IsReply)
            {
                SendScriptList(evt.UserId);
                var replyEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, true, LocalProfileId, UserDescription, UpdateChecker.Instance.CurrentVersion, sessionClient.HasFileChannel)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId,
                    CanDirect = CanDirectNow,
                    CanCatchUp = true
                };
                _ = sessionClient.SendAsync(null, replyEvt);

                if (FirstOrDefaultTimeline != null)
                    SendCursorMovedAsync(FirstOrDefaultTimeline.CurrentFrame);
            }
        }

        private bool versionMismatchHandled;

        private static bool IsCompatibleVersion(string peerVersion) =>
            Version.TryParse(peerVersion, out var peer)
            && Version.TryParse(UpdateChecker.Instance.CurrentVersion, out var mine)
            && peer == mine;

        private void HandleHostVersionMismatch(string hostVersion)
        {
            if (versionMismatchHandled) return;
            versionMismatchHandled = true;

            var hostIsNewer = !Version.TryParse(hostVersion, out var host)
                || !Version.TryParse(UpdateChecker.Instance.CurrentVersion, out var mine)
                || host > mine;
            var shownHostVersion = string.IsNullOrEmpty(hostVersion) ? Texts.Unknown : hostVersion;

            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                await StopNetworkAsync();
                versionMismatchHandled = false;

                if (hostIsNewer)
                {
                    PromptUpdate(string.Format(Texts.HostIsNewer, shownHostVersion, UpdateChecker.Instance.DisplayVersion));
                }
                else
                {
                    MessageBox.Show(
                        string.Format(Texts.HostIsOlder, shownHostVersion),
                        Texts.ConnectionError,
                        MessageBoxButton.OK);
                }
            });
        }

        private void HandlePeerDisconnected(Guid userId, bool isHost)
        {
            if (userId == LocalUserId)
            {
                BroadcastLocalPresence();
                return;
            }

            if (isHost && !IsKeptRoom)
            {
                Application.Current?.Dispatcher.InvokeAsync(() => BeginHostWatch(userId));
                return;
            }

            Application.Current?.Dispatcher.InvokeAsync(() => MarkDeparted(userId));
        }

        private static readonly TimeSpan DepartureDelay = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan HostWatchInterval = TimeSpan.FromMinutes(1);

        private readonly Dictionary<Guid, DateTime> departedAt = [];

        private Guid watchedHostId;

        private void MarkDeparted(Guid userId)
        {
            var participant = Participants.FirstOrDefault(p => p.UserId == userId);
            if (participant == null) return;

            participant.Status = UserStatus.Away;
            departedAt[userId] = DateTime.UtcNow;
        }

        private void RemoveParticipant(Guid userId)
        {
            departedAt.Remove(userId);

            var participant = Participants.FirstOrDefault(p => p.UserId == userId);
            if (participant == null) return;

            CommitParticipationTime(participant);
            Participants.Remove(participant);
            peerFileChannels.TryRemove(userId, out _);
            webParticipants.TryRemove(userId, out _);
            permissionOverrides.TryRemove(userId, out _);
        }

        private void RemoveLongDeparted()
        {
            var now = DateTime.UtcNow;
            foreach (var (userId, since) in departedAt.ToArray())
            {
                if (userId == watchedHostId || now - since < DepartureDelay) continue;

                RemoveParticipant(userId);
            }
        }

        private void BeginHostWatch(Guid hostUserId)
        {
            if (hostUserId == LocalUserId || watchedHostId == hostUserId) return;

            watchedHostId = hostUserId;
            MarkDeparted(hostUserId);

            _ = WatchHostAsync(hostUserId);
        }

        private void NotePeerAlive(Guid userId)
        {
            departedAt.Remove(userId);
            if (watchedHostId == userId) watchedHostId = Guid.Empty;
        }

        private async Task WatchHostAsync(Guid hostUserId)
        {
            var waitingSince = DateTime.UtcNow;

            while (true)
            {
                await Task.Delay(HostWatchInterval);

                if (disposed || !IsConnected || watchedHostId != hostUserId) return;
                if (DateTime.UtcNow - waitingSince < DepartureDelay) continue;

                var hostName = Participants.FirstOrDefault(p => p.UserId == hostUserId)?.UserName ?? Texts.Host;
                var keepWaiting = MessageBox.Show(
                    string.Format(Texts.HostNoResponse, hostName, DepartureDelay.TotalMinutes),
                    Texts.HostNoResponseTitle,
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes;

                if (disposed || !IsConnected || watchedHostId != hostUserId) return;

                if (!keepWaiting)
                {
                    watchedHostId = Guid.Empty;
                    AskWhatToKeepAfterHostLeft(Texts.HostDidNotReturn);
                    return;
                }

                waitingSince = DateTime.UtcNow;
            }
        }

        internal void HandleUserLeftEvent(UserLeftEvent evt)
        {
            if (evt.UserId == LocalUserId) return;
            if (evt.UserId != evt.ExecutorId) return;
            if (evt.IsHost && !IsFromHost(evt.UserId)) evt = evt with { IsHost = false };

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (evt.IsHost && !IsKeptRoom)
                {
                    AskWhatToKeepAfterHostLeft(Texts.HostClosedRoom);
                }
                else
                {
                    RemoveParticipant(evt.UserId);
                }
            });
        }

        private bool hostLeftHandled;
    }
}
