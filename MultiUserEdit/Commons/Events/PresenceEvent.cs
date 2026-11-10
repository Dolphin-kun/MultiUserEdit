using MultiUserEdit.Commons.Models;

namespace MultiUserEdit.Commons.Events
{
    public record PresenceEvent(Guid UserId, string UserName, UserRole Role, bool IsReply, Guid ProfileId = default, string Description = "", string Version = "", bool HasFileChannel = false) : EditEvent;
}
