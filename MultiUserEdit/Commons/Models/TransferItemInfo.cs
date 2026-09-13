using System.IO;

namespace MultiUserEdit.Commons.Models
{
    public class TransferItemInfo
    {
        public required string Name { get; init; }
        public required bool IsUpload { get; init; }
        public required long TotalBytes { get; init; }
        public required long TransferredBytes { get; init; }

        public string DisplayName => Path.GetFileName(Name.Replace('/', Path.DirectorySeparatorChar));

        public string DirectionText => IsUpload ? "送信" : "受信";

        public double Progress => TotalBytes > 0
            ? Math.Clamp((double)TransferredBytes / TotalBytes * 100.0, 0.0, 100.0)
            : 0.0;

        public string ProgressText => $"{Progress:F0}%";

        public string SizeText => $"{FormatBytes(TransferredBytes)} / {FormatBytes(TotalBytes)}";

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}
