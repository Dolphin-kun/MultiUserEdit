namespace MultiUserEdit.Commons.Events
{
    public record CharacterRequestEvent(
        string CharacterName,
        Guid RequesterId,
        bool IncludeFiles = false
    ) : EditEvent;
}
