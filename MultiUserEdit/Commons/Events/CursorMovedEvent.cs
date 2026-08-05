namespace MultiUserEdit.Commons.Events
{
    public record CursorMovedEvent(int CurrentFrame, int TimelineIndex = 0, bool IsPlaying = false) : EditEvent;
}