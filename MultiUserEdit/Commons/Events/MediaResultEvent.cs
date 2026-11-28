namespace MultiUserEdit.Commons.Events
{
    public record MediaResultEvent(Guid RequestId, string FileName, bool Success, string Message) : EditEvent;
}
