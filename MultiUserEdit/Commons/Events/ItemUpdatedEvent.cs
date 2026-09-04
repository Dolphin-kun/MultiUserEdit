namespace MultiUserEdit.Commons.Events
{
    public record ItemUpdatedEvent(Guid ItemId, int TimelineIndex, string ItemJson, IReadOnlyList<string>? MediaFileNames = null) : EditEvent;
}
