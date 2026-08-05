namespace MultiUserEdit.Commons.Events
{
    public record ItemRemovedEvent(Guid ItemId, int TimelineIndex) : EditEvent;
}
