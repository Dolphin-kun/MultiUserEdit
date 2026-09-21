namespace MultiUserEdit.Commons.Models
{
    public readonly record struct OperatingItem(Guid UserId, int TimelineIndex, DateTime LastActivity);
}
