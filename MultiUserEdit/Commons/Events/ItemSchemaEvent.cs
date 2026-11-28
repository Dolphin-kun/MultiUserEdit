namespace MultiUserEdit.Commons.Events
{
    public record ItemSchemaEvent(Guid RequestId, string TypeName, string Schema) : EditEvent;
}
