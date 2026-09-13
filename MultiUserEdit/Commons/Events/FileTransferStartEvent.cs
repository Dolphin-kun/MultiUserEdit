namespace MultiUserEdit.Commons.Events
{
    public record FileTransferStartEvent(
        Guid TransferId,
        string FileName,
        long FileSize,
        int TotalChunks,
        int ChunkSize = 0
    ) : EditEvent;
}
