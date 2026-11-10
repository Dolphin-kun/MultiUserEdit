using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Events;
using System.Diagnostics;

namespace MultiUserEdit.Networking
{
    internal sealed class SessionClient
    {
        private const string WebSocketEndpointBase = "wss://multi-user-edit.dolphin-discord-js.workers.dev";
        private const string RelayEndpointBase = "wss://mue-relay.ymm4-info.net";

        public const string LargeRoomPrefix = "big-";

        public const int SmallRoomLimit = 20;
        public const int LargeRoomLimit = 100;

        public static bool IsLargeRoom(string? roomId) =>
            roomId?.StartsWith(LargeRoomPrefix, StringComparison.OrdinalIgnoreCase) ?? false;

        public static string EndpointFor(string? roomId) =>
            IsLargeRoom(roomId) ? RelayEndpointBase : WebSocketEndpointBase;

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

        public event EventHandler<EditEvent>? EventReceived;
        public event Action? Disconnected;
        public event Action<string?, string?>? RoomNotFound;
        public event Action<string?>? UpdateAvailable;
        public event Action<Guid, bool>? PeerDisconnected;
        public event Action<bool>? ConnectionStateChanged;
        public event Action<Guid>? HostIdentified;

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
        }

        private readonly SemaphoreSlim stateLock = new(1, 1);

        public async Task StartAsync(string roomId, bool isHost, string? hostKey)
        {
            await stateLock.WaitAsync();
            try
            {
                if (IsConnected) return;

                var role = isHost ? "host" : "guest";
                var useRelay = IsLargeRoom(roomId);
                var url = $"{EndpointFor(roomId)}/?roomId={roomId}&role={role}&userId={LocalUserId}";

                try
                {
                    var headers = new Dictionary<string, string>
                    {
                        ["X-Client-Version"] = UpdateChecker.Instance.CurrentVersion
                    };
                    if (useRelay) headers["X-Batch"] = "1";
                    if (isHost && !string.IsNullOrEmpty(hostKey)) headers["X-Host-Key"] = hostKey;

                    await networkProvider.ConnectAsync(url, headers);
                    IsConnected = true;
                    LastSentAt = DateTime.Now;
                    LastReceivedAt = DateTime.Now;
                    BeginFileChannel(roomId, role, hostKey);
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
                    catch (Exception ex) { Debug.WriteLine($"[MultiUserEdit] file channel stop failed: {ex.Message}"); }
                }

                await networkProvider.DisconnectAsync();
                IsConnected = false;
            }
            finally
            {
                stateLock.Release();
            }

            ConnectionStateChanged?.Invoke(false);
        }

        public Task SendAsync(string? targetId, object data)
        {
            LastSentAt = DateTime.Now;
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

                Debug.WriteLine("[MultiUserEdit] file channel unavailable, falling back to the control channel");
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

            var url = $"{RelayEndpointBase}/?roomId={roomId}&role={role}&userId={LocalUserId}&channel=file";
            var headers = new Dictionary<string, string>
            {
                ["X-Client-Version"] = UpdateChecker.Instance.CurrentVersion,
                ["X-Batch"] = "1"
            };
            if (role == "host" && !string.IsNullOrEmpty(hostKey)) headers["X-Host-Key"] = hostKey;

            fileChannelRejected = false;

            try
            {
                await fileProvider.ConnectAsync(url, headers);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] file channel connect failed: {ex.Message}");
                return false;
            }

            await Task.Delay(FileChannelProbeDelay);

            if (generation != Volatile.Read(ref fileChannelGeneration))
            {
                try { await fileProvider.DisconnectAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[MultiUserEdit] file channel cleanup failed: {ex.Message}"); }
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
            EventReceived?.Invoke(this, editEvent);
        }

        private void HandleDisconnected()
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }

        private void HandleRoomNotFound(string? reason, string? latestVersion)
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(IsConnected);
            RoomNotFound?.Invoke(reason, latestVersion);
        }

        private void HandlePeerDisconnected(Guid userId, bool isHost)
        {
            PeerDisconnected?.Invoke(userId, isHost);
        }
    }
}
