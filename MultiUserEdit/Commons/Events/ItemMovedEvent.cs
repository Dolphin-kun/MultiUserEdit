namespace MultiUserEdit.Commons.Events
{
    public record ItemMovedEvent(Guid ItemId, int TimelineIndex, int Frame, int Length, int Layer) : EditEvent;
}
