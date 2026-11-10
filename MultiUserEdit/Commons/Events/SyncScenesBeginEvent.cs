namespace MultiUserEdit.Commons.Events
{
    public record SyncScenesBeginEvent(Guid SyncId, bool IsManual) : EditEvent;
}
