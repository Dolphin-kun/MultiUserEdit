namespace MultiUserEdit.Commons.Events
{
    public record ItemVisualsRequestEvent(Guid RequestId, int TimelineIndex, IReadOnlyList<Guid>? ItemIds = null) : EditEvent;
}
