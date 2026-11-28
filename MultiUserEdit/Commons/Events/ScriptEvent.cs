namespace MultiUserEdit.Commons.Events
{
    public record ScriptItem(Guid ScriptId, string CharacterName, string Serif, string AuthorName, Guid AuthorId, DateTime CreatedAt);

    public record ScriptEvent(string Action, ScriptItem Script) : EditEvent;

    public record ScriptListEvent(IReadOnlyList<ScriptItem> Scripts) : EditEvent;
}
