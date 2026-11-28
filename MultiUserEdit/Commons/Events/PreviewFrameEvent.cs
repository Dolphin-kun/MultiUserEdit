namespace MultiUserEdit.Commons.Events
{
    public record PreviewFrameEvent(Guid RequestId, Guid SceneId, int Frame, string Format, string Data, int Width, int Height) : EditEvent;
}
