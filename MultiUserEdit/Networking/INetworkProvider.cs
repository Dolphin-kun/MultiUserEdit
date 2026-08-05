using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    public interface INetworkProvider
    {
        Task ConnectAsync(string url);
        Task DisconnectAsync();
        Task SendAsync(string? targetId, object data);

        event EventHandler<EditEvent> EventReceived;
        event Action Disconnected;

        string LocalUserId { get; }
        string? RoomId { get; }
    }
}
