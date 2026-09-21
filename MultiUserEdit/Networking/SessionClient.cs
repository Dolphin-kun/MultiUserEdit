using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    internal sealed class SessionClient
    {
        private const string WebSocketEndpointBase = "wss://multi-user-edit.dolphin-discord-js.workers.dev";

        private readonly INetworkProvider networkProvider;

        public bool IsConnected { get; private set; }
        public Guid LocalUserId { get; }

        public DateTime LastSentAt { get; private set; } = DateTime.Now;

        public event EventHandler<EditEvent>? EventReceived;
        public event Action? Disconnected;
        public event Action<string?>? RoomNotFound;
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
                var url = $"{WebSocketEndpointBase}?roomId={roomId}&role={role}&userId={LocalUserId}";

                try
                {
                    var headers = new Dictionary<string, string>
                    {
                        ["X-Client-Version"] = UpdateChecker.Instance.CurrentVersion
                    };
                    if (isHost && !string.IsNullOrEmpty(hostKey)) headers["X-Host-Key"] = hostKey;

                    await networkProvider.ConnectAsync(url, headers);
                    IsConnected = true;
                    LastSentAt = DateTime.Now;
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

        private void HandleEventReceived(object? sender, EditEvent editEvent)
        {
            EventReceived?.Invoke(this, editEvent);
        }

        private void HandleDisconnected()
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }

        private void HandleRoomNotFound(string? reason)
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(IsConnected);
            RoomNotFound?.Invoke(reason);
        }

        private void HandlePeerDisconnected(Guid userId, bool isHost)
        {
            PeerDisconnected?.Invoke(userId, isHost);
        }
    }
}
