namespace MultiUserEdit.Commons.Events
{
    public record SyncScenesBeginEvent(Guid SyncId, bool IsManual, long BaseSeq = 0) : EditEvent;
}
