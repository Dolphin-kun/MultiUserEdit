using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        private void HandleDisconnected()
        {

            if (!isReconnecting) heardBeforeDrop = sessionClient.LastHeardAt;

            eventSender.ClearAllBaselines();

            if (disposed || isReconnecting || string.IsNullOrEmpty(RoomId))
            {
                ResetNetworkState();
                return;
            }

            _ = ReconnectAsync(RoomId, localUserRole);
        }

        private async Task ReconnectAsync(string targetRoomId, UserRole role)
        {
            isReconnecting = true;
            try
            {
                for (var attempt = 0; attempt < ReconnectDelays.Length; attempt++)
                {
                    await Task.Delay(ReconnectDelays[attempt]);

                    if (disposed || sessionClient.IsConnected || RoomId != targetRoomId) return;

                    try
                    {
                        await sessionClient.StartAsync(targetRoomId, role == UserRole.Host, hostKey);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!sessionClient.IsConnected) continue;

                    BroadcastLocalPresence();
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (FirstOrDefaultTimeline != null) SendCursorMovedAsync(FirstOrDefaultTimeline.CurrentFrame, force: true);
                    });
                    CatchUpAfterReconnect(role);
                    return;
                }

                ErrorNotifier.NotifyOnce(
                    Texts.ConnectionLostTitle,
                    Texts.ConnectionLostMessage);
                ResetNetworkState();
            }
            finally
            {
                isReconnecting = false;
            }
        }

        private static readonly TimeSpan CatchUpMargin = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan CatchUpMinWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan CatchUpQuietWait = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CatchUpMaxWait = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MaxCatchUpAway = TimeSpan.FromMinutes(30);
        private const int CatchUpBatchSize = 50;

        private readonly List<(DateTime At, EditEvent Event)> catchUpBuffer = [];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> catchUpPeers = new();
        private bool catchingUp;
        private bool receivedCatchUp;
        private DateTime lastCatchUpArrival;

        private void CatchUpAfterReconnect(UserRole role)
        {
            var now = DateTime.Now;
            var since = heardBeforeDrop - CatchUpMargin;
            if (now - since > MaxCatchUpAway) since = now - MaxCatchUpAway;

            var mine = sessionClient.OwnEditsSince(since);
            var peers = catchUpPeers.Keys.Where(id => id != LocalUserId).Select(id => id.ToString()).ToList();
            if (IsKeptRoom || peers.Count == 0)
            {
                foreach (var (_, edit) in mine) _ = sessionClient.ResendAsync(edit);
                CheckStateAfterReconnect(role);
                return;
            }

            SendCatchUpEntries(peers, mine, now);

            lock (catchUpBuffer)
            {
                catchUpBuffer.Clear();
                catchingUp = true;
                receivedCatchUp = false;
                lastCatchUpArrival = now;
            }

            _ = sessionClient.SendToManyAsync(peers, new CatchUpRequestEvent((now - since).TotalSeconds)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
            _ = FinishCatchUpAsync(role, since, now);
        }

        private async Task FinishCatchUpAsync(UserRole role, DateTime since, DateTime startedAt)
        {
            while (true)
            {
                await Task.Delay(250);
                var now = DateTime.Now;
                DateTime lastArrival;
                lock (catchUpBuffer) lastArrival = lastCatchUpArrival;
                if (now - startedAt >= CatchUpMaxWait) break;
                if (now - startedAt >= CatchUpMinWait && now - lastArrival >= CatchUpQuietWait) break;
            }

            List<(DateTime At, EditEvent Event)> pending;
            bool hadCatchUp;
            lock (catchUpBuffer)
            {
                catchingUp = false;
                hadCatchUp = receivedCatchUp;
                pending = [.. catchUpBuffer];
                catchUpBuffer.Clear();
            }

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (hadCatchUp) pending.AddRange(sessionClient.OwnEditsSince(since));
                ApplyCatchUp(pending);
                CheckStateAfterReconnect(role);
            });
        }

        private bool HoldForCatchUp(EditEvent editEvent)
        {
            lock (catchUpBuffer)
            {
                if (!catchingUp) return false;
                catchUpBuffer.Add((DateTime.Now, editEvent));
                return true;
            }
        }

        private void ApplyCatchUp(List<(DateTime At, EditEvent Event)> events)
        {
            var ordered = events
                .Select(entry => (Stamp: entry.Event.Stamp > 0 ? entry.Event.Stamp : EditClock.ToStamp(entry.At), entry.Event))
                .OrderBy(entry => entry.Stamp)
                .Select(entry => (entry.Stamp, Event: sessionClient.Clock.Filter(entry.Event, entry.Stamp)))
                .Where(entry => entry.Event != null)
                .ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var (stamp, edit) = ordered[index];
                sessionClient.Clock.Note(edit!, stamp);
                var suppress = index < ordered.Count - 1;
                if (edit!.ExecutorId == LocalUserId) DispatchEvent(edit, suppress);
                else ApplyEvent(new ReplayedEvent(edit), suppress);
            }
        }

        private void SendCatchUpEntries(IReadOnlyList<string> targets, List<(DateTime At, EditEvent Event)> edits, DateTime now)
        {
            foreach (var chunk in edits.Chunk(CatchUpBatchSize))
            {
                var entries = chunk.Select(entry => new CatchUpEntry((long)(now - entry.At).TotalMilliseconds, entry.Event)).ToList();
                _ = sessionClient.SendToManyAsync(targets, new CatchUpEvent(entries)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                });
            }
        }

        private void SendCatchUp(CatchUpRequestEvent request)
        {
            if (!IsConnected || IsKeptRoom) return;

            var away = Math.Clamp(request.AwaySeconds, 0, MaxCatchUpAway.TotalSeconds);
            var now = DateTime.Now;
            var mine = sessionClient.OwnEditsSince(now - TimeSpan.FromSeconds(away));
            if (mine.Count > 0) SendCatchUpEntries([request.ExecutorId.ToString()], mine, now);
        }

        private void ReceiveCatchUp(CatchUpEvent catchUp)
        {
            var now = DateTime.Now;
            var maxAge = (long)MaxCatchUpAway.TotalMilliseconds;
            var events = (catchUp.Entries ?? [])
                .Where(entry => entry?.Event != null
                    && entry.Event.ExecutorId == catchUp.ExecutorId
                    && SyncScenesTransfer.IsProjectEdit(entry.Event)
                    && entry.Event is not SceneRemovedEvent)
                .Select(entry => (now - TimeSpan.FromMilliseconds(Math.Clamp(entry.AgeMs, 0, maxAge)), entry.Event))
                .ToList();
            if (events.Count == 0) return;

            lock (catchUpBuffer)
            {
                if (catchingUp)
                {
                    catchUpBuffer.AddRange(events);
                    receivedCatchUp = true;
                    lastCatchUpArrival = now;
                    return;
                }
            }

            Application.Current?.Dispatcher.InvokeAsync(() => ApplyCatchUp(events));
        }

        private void CheckStateAfterReconnect(UserRole role)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (!IsConnected || IsKeptRoom) return;

                if (role == UserRole.Host)
                {
                    var digest = ComputeStateDigest();
                    if (digest == null) return;

                    _ = sessionClient.SendAsync(null, new StateDigestEvent(digest)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    });
                    return;
                }

                var host = Participants.FirstOrDefault(p => p.Role == UserRole.Host && p.UserId != LocalUserId);
                _ = sessionClient.SendAsync(host?.UserId.ToString(), new StateDigestRequestEvent
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                });
            });
        }

        internal void HandleStateDigestRequest(StateDigestRequestEvent evt)
        {
            if (!IsConnected || !IsHost) return;

            var digest = ComputeStateDigest();
            if (digest == null) return;

            _ = sessionClient.SendAsync(evt.ExecutorId.ToString(), new StateDigestEvent(digest)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
        }

        private bool hasDivergenceWarning;
        public bool HasDivergenceWarning
        {
            get => hasDivergenceWarning;
            private set => Set(ref hasDivergenceWarning, value);
        }

        private string divergenceWarningText = string.Empty;
        public string DivergenceWarningText
        {
            get => divergenceWarningText;
            private set => Set(ref divergenceWarningText, value);
        }

        public void DismissDivergenceWarning() => HasDivergenceWarning = false;

        internal void HandleStateDigest(StateDigestEvent evt)
        {
            if (!IsConnected || IsHost) return;

            var host = Participants.FirstOrDefault(p => p.UserId == evt.ExecutorId && p.Role == UserRole.Host);
            if (host == null) return;

            var digest = ComputeStateDigest();
            if (digest == null) return;

            var matched = digest == evt.Digest;
            var hostName = host.UserName;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (matched)
                {
                    HasDivergenceWarning = false;
                    return;
                }

                DivergenceWarningText = string.Format(Texts.DivergenceWarning, hostName);
                HasDivergenceWarning = true;
            });
        }

        private static readonly string[] LocallyGeneratedVoiceProperties =
            ["VoiceCache", "Pronounce", "LipSyncFrames", "LipSyncSourceFileStamp", "VoiceLength"];

        private static string WithoutLocallyGeneratedVoice(string json)
        {
            try
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                foreach (var name in LocallyGeneratedVoiceProperties) root.Remove(name);
                return root.ToString(Formatting.None);
            }
            catch
            {
                return json;
            }
        }

        private string? ComputeStateDigest()
        {
            if (Scenes == null) return null;

            try
            {
                var builder = new StringBuilder();
                foreach (var timeline in Scenes.Timelines)
                {
                    builder.Append(timeline.ID).Append('|').Append(timeline.Name);
                    if (timeline.VideoInfo is { } info)
                    {
                        builder.Append('|').Append(info.Width).Append('x').Append(info.Height)
                            .Append('@').Append(info.FPS).Append('/').Append(info.Hz)
                            .Append('|').Append(VideoInfoSerializer.ToText(info.BackgroundColor));
                    }
                    builder.Append('\n');

                    var lines = new List<string>();
                    foreach (var item in timeline.Items)
                    {
                        var json = JsonConvert.SerializeObject(item, ItemSerializerOptions.NullPath);
                        if (item is VoiceItem) json = WithoutLocallyGeneratedVoice(json);
                        var itemHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                        lines.Add($"{ItemIdManager.GetOrCreateId(item)}|{item.Frame}|{item.Layer}|{item.Length}|{itemHash}");
                    }

                    lines.Sort(StringComparer.Ordinal);
                    foreach (var line in lines) builder.Append(line).Append('\n');
                }

                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
            }
            catch
            {
                return null;
            }
        }

        private bool updateNoticeShown;
    }
}
