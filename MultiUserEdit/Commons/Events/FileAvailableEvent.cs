namespace MultiUserEdit.Commons.Events
{
    public record FileAvailableEvent(
        Guid TransferId,
        string FileName,
        long FileSize,
        string Hash,
        string? CharacterName = null
    ) : EditEvent;
}
