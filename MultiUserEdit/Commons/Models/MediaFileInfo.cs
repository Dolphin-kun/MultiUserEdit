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
        public string OwnerName { get; set; } = string.Empty;

        public string DirectionText => IsSent ? Texts.Send : Texts.Receive;

        public string OwnerText => IsSent
            ? Texts.Me
            : string.IsNullOrEmpty(OwnerName) ? Texts.Unknown : OwnerName;

        public string StatusText => !IsSent
            ? Texts.ReceivedDone
            : WasTransferred ? Texts.SentDone : Texts.SendSkipped;

        public bool CanDelete => !IsSent;

        public string FormattedSize => SizeBytes switch
        {
            > 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F2} MB",
            > 1024 => $"{SizeBytes / 1024.0:F1} KB",
            _ => $"{SizeBytes} B"
        };
    }
}
