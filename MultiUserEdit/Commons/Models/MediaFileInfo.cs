namespace MultiUserEdit.Commons.Models
{
    public class MediaFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }

        public string FormattedSize => SizeBytes switch
        {
            > 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F2} MB",
            > 1024 => $"{SizeBytes / 1024.0:F1} KB",
            _ => $"{SizeBytes} B"
        };
    }
}
