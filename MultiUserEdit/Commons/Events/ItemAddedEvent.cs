namespace MultiUserEdit.Commons.Events
{
    public record ItemAddedEvent(Guid ItemId, int TimelineIndex, string ItemTypeName, string ItemJson, int Frame, int Layer, IReadOnlyList<string>? MediaFileNames = null) : EditEvent;
}
