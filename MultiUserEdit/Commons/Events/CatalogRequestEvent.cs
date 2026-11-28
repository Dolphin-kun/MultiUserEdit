namespace MultiUserEdit.Commons.Events
{
    public record CatalogRequestEvent(Guid RequestId, string Kind, string? Argument = null) : EditEvent;
}
