namespace MultiUserEdit.Commons.Events
{
    public record ItemLockedEvent(Guid ItemId, Guid UserId, long LockTimestamp = 0) : EditEvent;
}
