namespace MultiUserEdit.Commons.Events
{
    internal sealed record ReplayedEvent(EditEvent Inner) : EditEvent;
}
