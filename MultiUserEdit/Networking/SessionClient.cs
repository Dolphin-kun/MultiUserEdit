using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    internal sealed class SessionClient
    {
        private const string WebSocketEndpointBase = "wss://multi-user-edit.dolphin-discord-js.workers.dev";

        private readonly INetworkProvider networkProvider;

        public bool IsConnected { get; private set; }
        public Guid LocalUserId { get; }

        public event EventHandler<EditEvent>? EventReceived;
        public event Action? Disconnected;
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
        }

        public async Task StartAsync(string roomId)
        {
            if (IsConnected) return;

            var url = $"{WebSocketEndpointBase}?roomId={roomId}";

            try
            {
                await networkProvider.ConnectAsync(url);
                IsConnected = true;
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
    }
}
