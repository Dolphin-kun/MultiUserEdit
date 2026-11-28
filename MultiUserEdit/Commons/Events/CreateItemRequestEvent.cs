namespace MultiUserEdit.Commons.Events
{
    public record CreateItemRequestEvent(Guid RequestId, Guid SceneId, int TimelineIndex, string Kind, int Frame, int Layer, string? FileName = null) : EditEvent;
}
