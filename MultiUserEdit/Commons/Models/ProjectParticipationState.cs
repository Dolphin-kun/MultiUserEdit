namespace MultiUserEdit.Commons.Models
{
    public class ProjectParticipationState
    {
        public Dictionary<string, ParticipationRecord> Participants { get; set; } = [];
    }

    public class ParticipationRecord
    {
        public string UserName { get; set; } = string.Empty;
        public double TotalSeconds { get; set; }
    }
}
