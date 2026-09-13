namespace MultiUserEdit.Commons.Models
{
    public class OnlineTimeline
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<OnlineItem> Items { get; set; } = [];
        public int CurrentFrame { get; set; }
        public int Length { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FPS { get; set; }
        public int Hz { get; set; }
        public string? BackgroundColor { get; set; }
    }
}
