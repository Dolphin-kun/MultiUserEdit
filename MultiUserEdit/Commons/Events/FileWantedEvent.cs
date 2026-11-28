namespace MultiUserEdit.Commons.Events
{
    public record FileWantedEvent(IReadOnlyList<string> FileNames) : EditEvent;
}
