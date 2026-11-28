namespace MultiUserEdit.Commons.Events
{
    public record SceneReadiness(int Index, Guid SceneId, int MissingItems, int MissingMedia);

    public record RenderStatusEvent(bool CanRender, int Viewers, double RenderMs, int SuggestedIntervalMs, IReadOnlyList<SceneReadiness> Scenes) : EditEvent
    {
        public bool CanStream { get; init; }
    }
}
