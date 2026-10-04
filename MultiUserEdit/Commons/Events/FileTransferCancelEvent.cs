namespace MultiUserEdit.Commons.Events
{
    public record FileTransferCancelEvent(
        Guid TransferId,
        Guid CancelledBy
    ) : EditEvent;
}
