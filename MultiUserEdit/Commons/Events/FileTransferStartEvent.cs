namespace MultiUserEdit.Commons.Events
{
    public record FileTransferStartEvent(
        Guid TransferId,
        string FileName,
        long FileSize,
        int TotalChunks
    ) : EditEvent;
}
