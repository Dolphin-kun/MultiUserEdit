namespace MultiUserEdit.Commons.Events
{
    public record UserKickedEvent(Guid TargetUserId) : EditEvent;
}
