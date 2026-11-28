namespace MultiUserEdit.Commons.Events
{
    public record ItemSchemaRequestEvent(Guid RequestId, string TypeName) : EditEvent;
}
