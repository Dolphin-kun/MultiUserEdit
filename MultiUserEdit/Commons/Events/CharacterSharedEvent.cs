namespace MultiUserEdit.Commons.Events
{
    public record CharacterSharedEvent(
        string CharacterName,
        string CharacterJson,
        IReadOnlyList<string>? MediaFileNames = null
    ) : EditEvent;
}
