namespace MultiUserEdit.Commons.Events
{
    public record FilePullEvent(string FileName) : EditEvent;
}
