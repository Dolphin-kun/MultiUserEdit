namespace MultiUserEdit.Commons.Events
{
    public record SceneAddedEvent(int Index, string Name) : EditEvent;
}
