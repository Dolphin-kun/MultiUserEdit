namespace MultiUserEdit.Commons.Events
{
    public record CreateItemResultEvent(Guid RequestId, bool Success, Guid ItemId, string Message) : EditEvent;
}
