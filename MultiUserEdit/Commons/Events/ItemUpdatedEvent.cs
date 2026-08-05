namespace MultiUserEdit.Commons.Events
{
    public record ItemUpdatedEvent(Guid ItemId, int TimelineIndex, string ItemJson) : EditEvent;
}
