namespace MultiUserEdit.Commons.Events
{
    public record FileChunkAckEvent(Guid TransferId, int ChunkIndex) : EditEvent;
}
