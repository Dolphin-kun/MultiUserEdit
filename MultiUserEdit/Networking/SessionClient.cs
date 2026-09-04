using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    internal sealed class SessionClient
    {
        private const string WebSocketEndpointBase = "wss://multi-user-edit.dolphin-discord-js.workers.dev";

        private readonly INetworkProvider networkProvider;

        public bool IsConnected { get; private set; }
        public Guid LocalUserId { get; }

        // 最後にイベントを送信した時刻。他の参加者は受信イベントで在席を判定しているため、
        // 自分自身の在席判定も同じ基準（＝送信の有無）に揃えるために使う。
        public DateTime LastSentAt { get; private set; } = DateTime.Now;

        public event EventHandler<EditEvent>? EventReceived;
        public event Action? Disconnected;
        public event Action? RoomNotFound;
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

        public async Task StartAsync(string roomId, bool isHost)
        {
            if (IsConnected) return;

            var role = isHost ? "host" : "guest";
            var url = $"{WebSocketEndpointBase}?roomId={roomId}&role={role}&userId={LocalUserId}";

            try
            {
                await networkProvider.ConnectAsync(url);
                IsConnected = true;
                LastSentAt = DateTime.Now;
                ConnectionStateChanged?.Invoke(IsConnected);
            }
            catch
            {
                IsConnected = false;
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (!IsConnected) return;

            await networkProvider.DisconnectAsync();

            IsConnected = false;
            ConnectionStateChanged?.Invoke(IsConnected);
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
            ConnectionStateChanged?.Invoke(IsConnected);
            Disconnected?.Invoke();
        }

        private void HandleRoomNotFound()
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(IsConnected);
            RoomNotFound?.Invoke();
        }

        private void HandlePeerDisconnected(Guid userId, bool isHost)
        {
            PeerDisconnected?.Invoke(userId, isHost);
        }
    }
}
