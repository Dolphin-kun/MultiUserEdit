namespace MultiUserEdit.Commons.Events
{
    public record SyncRequestEvent(Guid? SourceUserId = null, bool IsManual = false) : EditEvent;
}
