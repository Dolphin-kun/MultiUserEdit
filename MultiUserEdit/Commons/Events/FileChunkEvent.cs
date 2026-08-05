namespace MultiUserEdit.Commons.Events
{
    public record FileChunkEvent(Guid TransferId, int ChunkIndex, int TotalChunks, string Data) : EditEvent;
}
