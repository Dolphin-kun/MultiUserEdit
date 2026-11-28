namespace MultiUserEdit.Commons.Events
{
    public record RtcSignalEvent(Guid SessionId, string Kind, string Data, string? Mid = null) : EditEvent;
}
