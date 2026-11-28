namespace MultiUserEdit.Commons.Events
{
    internal sealed record SequenceMarkerEvent(long Seq) : EditEvent;
}
