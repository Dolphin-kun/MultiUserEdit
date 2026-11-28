namespace MultiUserEdit.Commons.Events
{
    public record ItemVisualsEvent(Guid RequestId, int TimelineIndex, string Items) : EditEvent;
}
