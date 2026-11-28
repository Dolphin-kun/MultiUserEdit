namespace MultiUserEdit.Commons.Events
{
    public record CatalogEvent(Guid RequestId, string Kind, string Json) : EditEvent;
}
