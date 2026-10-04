using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Events;

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

        private readonly INetworkProvider networkProvider;

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

        public SessionClient(INetworkProvider networkProvider)
        {
            this.networkProvider = networkProvider;
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
