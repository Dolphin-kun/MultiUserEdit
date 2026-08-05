namespace MultiUserEdit.Commons.Events
{
    public record UserLeftEvent(Guid UserId, bool IsHost) : EditEvent;
}
