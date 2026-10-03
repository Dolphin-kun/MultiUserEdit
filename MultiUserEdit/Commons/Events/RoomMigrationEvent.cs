namespace MultiUserEdit.Commons.Events
{
    public record RoomMigrationEvent(string NewRoomId, string Phase) : EditEvent;
}
