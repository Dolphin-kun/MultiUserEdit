namespace MultiUserEdit.Commons.Events
{
    public record RoomMigrationAckEvent(bool Success) : EditEvent;
}
