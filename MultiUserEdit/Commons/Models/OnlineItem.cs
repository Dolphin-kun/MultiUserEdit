namespace MultiUserEdit.Commons.Models
{
    public class OnlineItem
    {
        public Guid ItemId { get; set; }
        public string ItemTypeName { get; set; } = string.Empty;
        public string ItemJson { get; set; } = string.Empty;
        public List<string>? MediaFileNames { get; set; }
        public int Frame { get; set; }
        public int Layer { get; set; }
    }
}
