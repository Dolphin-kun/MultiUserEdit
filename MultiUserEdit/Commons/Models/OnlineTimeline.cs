namespace MultiUserEdit.Commons.Models
{
    public class OnlineTimeline
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<OnlineItem> Items { get; set; } = [];
        public int CurrentFrame { get; set; }
        public int Length { get; set; }
    }
}
