namespace MultiUserEdit.Commons.Events
{
    public record SyncScenesChunkEvent(Guid SyncId, int ChunkIndex, int TotalChunks, string Data, bool IsManual) : EditEvent;
}
