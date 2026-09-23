using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    public interface INetworkProvider
    {
        Task ConnectAsync(string url, IReadOnlyDictionary<string, string> headers);
        Task DisconnectAsync();
        Task SendAsync(string? targetId, object data);

        event EventHandler<EditEvent> EventReceived;
        event Action Disconnected;
        event Action<string?, string?> RoomNotFound;
        event Action<string?> UpdateAvailable;
        event Action<Guid, bool> PeerDisconnected;

        string LocalUserId { get; }
        string? RoomId { get; }
    }
}
