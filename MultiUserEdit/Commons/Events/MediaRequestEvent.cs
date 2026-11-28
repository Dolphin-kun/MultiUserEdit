namespace MultiUserEdit.Commons.Events
{
    public record MediaRequestEvent(Guid RequestId, string FileName, bool AudioOnly = false) : EditEvent;
}
