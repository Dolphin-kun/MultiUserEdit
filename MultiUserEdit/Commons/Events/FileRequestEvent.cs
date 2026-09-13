namespace MultiUserEdit.Commons.Events
{
    public record FileRequestEvent(
        Guid TransferId,
        Guid RequesterId,
        bool NeedsTransfer = true
    ) : EditEvent;
}
