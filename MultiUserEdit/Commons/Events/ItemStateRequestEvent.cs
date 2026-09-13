namespace MultiUserEdit.Commons.Events
{
    public record ItemStateRequestEvent(Guid ItemId, Guid RequesterId) : EditEvent;
}
