using MultiUserEdit.Commons.Models;

namespace MultiUserEdit.Commons.Events
{
    public record PermissionUpdatedEvent(Guid? TargetUserId, UserPermission Permission) : EditEvent;
}
