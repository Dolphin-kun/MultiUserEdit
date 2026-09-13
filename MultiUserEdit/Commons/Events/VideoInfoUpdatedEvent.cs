namespace MultiUserEdit.Commons.Events
{
    public record VideoInfoUpdatedEvent(int TimelineIndex, int Width, int Height, int FPS, int Hz, string? BackgroundColor = null) : EditEvent;
}
