namespace MultiUserEdit.Commons.Models
{
    public class MediaFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsSent { get; set; }
        public bool WasTransferred { get; set; } = true;

        public string DirectionText => IsSent ? "送信" : "受信";

        public string StatusText => !IsSent
            ? "受信済み"
            : WasTransferred ? "送信済み" : "相手が保有済みのため送信を省略";

        public bool CanDelete => !IsSent;

        public string FormattedSize => SizeBytes switch
        {
            > 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F2} MB",
            > 1024 => $"{SizeBytes / 1024.0:F1} KB",
            _ => $"{SizeBytes} B"
        };
    }
}
