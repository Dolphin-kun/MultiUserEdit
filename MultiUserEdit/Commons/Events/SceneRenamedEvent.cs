namespace MultiUserEdit.Commons.Events
{
    public record SceneRenamedEvent(int TimelineIndex, string NewName) : EditEvent;
}
