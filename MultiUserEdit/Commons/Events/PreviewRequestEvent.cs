namespace MultiUserEdit.Commons.Events
{
    public record PreviewRequestEvent(Guid RequestId, Guid SceneId, int TimelineIndex, int Frame, int Width, bool Lossless = false) : EditEvent;
}
