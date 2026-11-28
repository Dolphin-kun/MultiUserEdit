namespace MultiUserEdit.Commons.Events
{
    public record CatchUpRequestEvent(double AwaySeconds) : EditEvent;

    public record CatchUpEntry(long AgeMs, EditEvent Event);

    public record CatchUpEvent(List<CatchUpEntry> Entries) : EditEvent;
}
