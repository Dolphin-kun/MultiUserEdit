namespace MultiUserEdit.Commons.Events
{
    public record FileOfferEvent(IReadOnlyList<string> FileNames) : EditEvent;
}
