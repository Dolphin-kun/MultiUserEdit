namespace MultiUserEdit.Commons.Events
{
    public record ItemUnlockedEvent(Guid ItemId, Guid UserId) : EditEvent;
}
