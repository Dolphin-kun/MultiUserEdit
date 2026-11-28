using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    internal sealed class SessionClient
    {
        private const string WebSocketEndpointBase = "wss://multi-user-edit.dolphin-discord-js.workers.dev";
        private const string RelayEndpointBase = "wss://mue-relay.ymm4-info.net";
        public const string RelayStatusUrl = "https://mue-relay.ymm4-info.net/status";

        public static readonly Guid ServerUserId = Guid.Parse("5e5e5e5e-0000-4000-8000-000000000001");


        public bool UsesBackupServer { get; set; }

        public string? HostRoomKind { get; set; }

        private static readonly TimeSpan FileChannelProbeDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan FileChannelRetryDelay = TimeSpan.FromSeconds(3);
        private const int FileChannelAttempts = 5;

        private readonly INetworkProvider networkProvider;
        private readonly Func<string, INetworkProvider>? fileProviderFactory;
        private INetworkProvider? fileProvider;
        private int fileChannelGeneration;
        private volatile bool fileChannelRejected;

        public bool HasFileChannel { get; private set; }

        public event Action? FileChannelChanged;

        public bool IsConnected { get; private set; }
        public Guid LocalUserId { get; }

        public DateTime LastSentAt { get; private set; } = DateTime.Now;
        public DateTime LastReceivedAt { get; private set; } = DateTime.Now;
        public DateTime LastHeardAt { get; private set; } = DateTime.Now;
        public bool InRoom { get; private set; }

        private static readonly TimeSpan OwnEditRetention = TimeSpan.FromMinutes(30);
        private const int MaxOwnEdits = 5000;
        private readonly List<(DateTime At, EditEvent Event)> ownEdits = [];
        private string? ownEditsRoomId;

        public List<(DateTime At, EditEvent Event)> OwnEditsSince(DateTime since)
        {
            lock (ownEdits) return [.. ownEdits.Where(entry => entry.At >= since)];
        }

        public EditClock Clock { get; } = new();

        private readonly string userKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        private void RecordOwnEdit(EditEvent edit)
        {
            var now = DateTime.Now;
            if (edit is SceneRemovedEvent) return;
            lock (ownEdits)
            {
                ownEdits.Add((now, edit));
                var cutoff = DateTime.Now - OwnEditRetention;
                var stale = ownEdits.FindIndex(entry => entry.At >= cutoff);
                if (stale > 0) ownEdits.RemoveRange(0, stale);
                else if (stale < 0) ownEdits.Clear();
                if (ownEdits.Count > MaxOwnEdits) ownEdits.RemoveRange(0, ownEdits.Count - MaxOwnEdits);
            }
        }

        public Task ResendAsync(EditEvent edit) => networkProvider.SendAsync(null, edit);

        public event EventHandler<EditEvent>? EventReceived;
        public event Action? Disconnected;
        public event Action<string?, string?>? RoomNotFound;
        public event Action<string?>? UpdateAvailable;
        public event Action<Guid, bool>? PeerDisconnected;
        public event Action<bool>? ConnectionStateChanged;
        public event Action<Guid>? HostIdentified;
        public event Action<bool>? KeptRoomJoined;
        public event Action? SnapshotRequested;
        public event Action? ResyncRequired;

        public bool HasUnacknowledgedEdits => networkProvider.HasUnacknowledgedEdits;

        public SessionClient(INetworkProvider networkProvider, Func<string, INetworkProvider>? fileProviderFactory = null)
        {
            this.networkProvider = networkProvider;
            this.fileProviderFactory = fileProviderFactory;
            if (!Guid.TryParse(networkProvider.LocalUserId, out var localUserId))
            {
                localUserId = Guid.NewGuid();
            }
            LocalUserId = localUserId;
            this.networkProvider.EventReceived += HandleEventReceived;
            this.networkProvider.Disconnected += HandleDisconnected;
            this.networkProvider.RoomNotFound += HandleRoomNotFound;
            this.networkProvider.UpdateAvailable += version => UpdateAvailable?.Invoke(version);
            this.networkProvider.PeerDisconnected += HandlePeerDisconnected;
            this.networkProvider.RoomInfoReceived += hostUserId =>
            {
                if (Guid.TryParse(hostUserId, out var hostId) && hostId != Guid.Empty) HostIdentified?.Invoke(hostId);
            };
            this.networkProvider.KeptRoomInfoReceived += (_, hasSnapshot) => KeptRoomJoined?.Invoke(hasSnapshot);
            this.networkProvider.SnapshotRequested += () => SnapshotRequested?.Invoke();
            this.networkProvider.ResyncRequired += () => ResyncRequired?.Invoke();
        }

        private readonly SemaphoreSlim stateLock = new(1, 1);

        public async Task StartAsync(string roomId, bool isHost, string? hostKey)
        {
            await stateLock.WaitAsync();
            try
            {
                if (IsConnected) return;

                var attempt = Interlocked.Increment(ref connectAttempt);

                if (ownEditsRoomId != roomId)
                {
                    lock (ownEdits) ownEdits.Clear();
                    ownEditsRoomId = roomId;
                }

                var role = isHost ? "host" : "guest";
                var useRelay = !UsesBackupServer;
                var url = $"{(useRelay ? RelayEndpointBase : WebSocketEndpointBase)}/?roomId={Uri.EscapeDataString(roomId)}&role={role}&userId={LocalUserId}";
                if (isHost && useRelay && !string.IsNullOrEmpty(HostRoomKind)) url += $"&kind={HostRoomKind}";

                try
                {
                    var headers = new Dictionary<string, string>
                    {
                        ["X-Client-Version"] = UpdateChecker.Instance.CurrentVersion
                    };
                    if (useRelay) headers["X-Batch"] = "1";
                    if (isHost && !string.IsNullOrEmpty(hostKey)) headers["X-Host-Key"] = hostKey;
                    headers["X-User-Key"] = userKey;

                    await networkProvider.ConnectAsync(url, headers);
                    if (Volatile.Read(ref endedAttempt) == attempt) return;

                    IsConnected = true;
                    InRoom = true;
                    LastSentAt = DateTime.Now;
                    LastReceivedAt = DateTime.Now;
                    if (useRelay) BeginFileChannel(roomId, role, hostKey);
                }
                catch
                {
                    IsConnected = false;
                    throw;
                }
            }
            finally
            {
                stateLock.Release();
            }

            ConnectionStateChanged?.Invoke(true);
        }

        public async Task StopAsync()
        {
            await stateLock.WaitAsync();
            try
            {
                Interlocked.Increment(ref fileChannelGeneration);
                HasFileChannel = false;

                if (fileProvider != null)
                {
                    try { await fileProvider.DisconnectAsync(); }
                    catch { }
                }

                await networkProvider.DisconnectAsync();
                IsConnected = false;
                InRoom = false;
            }
            finally
            {
                stateLock.Release();
            }

            ConnectionStateChanged?.Invoke(false);
        }

        public DateTime LastProjectEditAt { get; private set; } = DateTime.MinValue;

        public Task SendAsync(string? targetId, object data)
        {
            LastSentAt = DateTime.Now;
            if (targetId == null && data is EditEvent edit && SyncScenesTransfer.IsProjectEdit(edit))
            {
                LastProjectEditAt = DateTime.Now;
                var stamped = Clock.Stamp(edit);
                RecordOwnEdit(stamped);
                data = stamped;
            }
            return networkProvider.SendAsync(targetId, data);
        }

        public Task SendFileDataAsync(string? targetId, object data)
        {
            LastSentAt = DateTime.Now;

            return HasFileChannel && fileProvider != null
                ? fileProvider.SendAsync(targetId, data)
                : networkProvider.SendAsync(targetId, data);
        }

        public Task SendFileDataToManyAsync(IReadOnlyList<string> targetIds, object data)
        {
            if (targetIds.Count == 0) return Task.CompletedTask;

            LastSentAt = DateTime.Now;

            return HasFileChannel && fileProvider != null
                ? fileProvider.SendToManyAsync(targetIds, data)
                : networkProvider.SendToManyAsync(targetIds, data);
        }

        private void BeginFileChannel(string roomId, string role, string? hostKey)
        {
            if (fileProviderFactory == null) return;

            var generation = Interlocked.Increment(ref fileChannelGeneration);

            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < FileChannelAttempts; attempt++)
                {
                    if (generation != Volatile.Read(ref fileChannelGeneration) || !IsConnected) return;
                    if (await TryConnectFileChannelAsync(roomId, role, hostKey, generation)) return;

                    await Task.Delay(FileChannelRetryDelay);
                }

            });
        }

        private async Task<bool> TryConnectFileChannelAsync(string roomId, string role, string? hostKey, int generation)
        {
            if (fileProviderFactory == null) return true;

            if (fileProvider == null)
            {
                fileProvider = fileProviderFactory(LocalUserId.ToString());
                fileProvider.EventReceived += HandleEventReceived;
                fileProvider.Disconnected += HandleFileChannelLost;
                fileProvider.RoomNotFound += (_, _) => HandleFileChannelLost();
            }

            var url = $"{RelayEndpointBase}/?roomId={Uri.EscapeDataString(roomId)}&role={role}&userId={LocalUserId}&channel=file";
            var headers = new Dictionary<string, string>
            {
                ["X-Client-Version"] = UpdateChecker.Instance.CurrentVersion,
                ["X-Batch"] = "1"
            };
            if (role == "host" && !string.IsNullOrEmpty(hostKey)) headers["X-Host-Key"] = hostKey;
            headers["X-User-Key"] = userKey;

            fileChannelRejected = false;

            try
            {
                await fileProvider.ConnectAsync(url, headers);
            }
            catch
            {
                return false;
            }

            await Task.Delay(FileChannelProbeDelay);

            if (generation != Volatile.Read(ref fileChannelGeneration))
            {
                try { await fileProvider.DisconnectAsync(); }
                catch { }
                return false;
            }

            if (fileChannelRejected) return false;

            HasFileChannel = true;
            FileChannelChanged?.Invoke();
            return true;
        }

        private void HandleFileChannelLost()
        {
            fileChannelRejected = true;
            if (!HasFileChannel) return;

            HasFileChannel = false;
            FileChannelChanged?.Invoke();
        }

        public Task SendToManyAsync(IReadOnlyList<string> targetIds, object data)
        {
            LastSentAt = DateTime.Now;
            return networkProvider.SendToManyAsync(targetIds, data);
        }

        private void HandleEventReceived(object? sender, EditEvent editEvent)
        {
            LastReceivedAt = DateTime.Now;
            LastHeardAt = DateTime.Now;
            EventReceived?.Invoke(this, editEvent);
        }

        private void HandleDisconnected()
        {
            Volatile.Write(ref endedAttempt, Volatile.Read(ref connectAttempt));
            IsConnected = false;
            Disconnected?.Invoke();
        }

        private int connectAttempt;
        private int endedAttempt;

        private void HandleRoomNotFound(string? reason, string? latestVersion)
        {
            Volatile.Write(ref endedAttempt, Volatile.Read(ref connectAttempt));
            IsConnected = false;
            InRoom = false;
            ConnectionStateChanged?.Invoke(IsConnected);
            RoomNotFound?.Invoke(reason, latestVersion);
        }

        private void HandlePeerDisconnected(Guid userId, bool isHost)
        {
            PeerDisconnected?.Invoke(userId, isHost);
        }
    }
}
