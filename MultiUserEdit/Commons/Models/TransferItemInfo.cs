using System.IO;
using System.Linq;

namespace MultiUserEdit.Commons.Models
{
    public class TransferItemInfo
    {
        public Guid TransferId { get; init; }

        public required string Name { get; init; }
        public required bool IsUpload { get; init; }
        public required long TotalBytes { get; init; }
        public required long TransferredBytes { get; init; }
        public double BytesPerSecond { get; init; }
        public string OwnerName { get; init; } = string.Empty;

        public string DisplayName => Path.GetFileName(Name.Replace('/', Path.DirectorySeparatorChar));

        public string DirectionText => IsUpload ? "送信" : "受信";

        public bool CanCancel => !IsUpload && TransferId != Guid.Empty;

        public double Progress => TotalBytes > 0
            ? Math.Clamp((double)TransferredBytes / TotalBytes * 100.0, 0.0, 100.0)
            : 0.0;

        public string ProgressText => $"{Progress:F0}%";

        public string SizeText => $"{FormatBytes(TransferredBytes)} / {FormatBytes(TotalBytes)}";

        public string SpeedText => BytesPerSecond <= 0 ? string.Empty : $"{FormatBytes((long)BytesPerSecond)}/秒";

        public string RemainingText
        {
            get
            {
                if (BytesPerSecond <= 0 || TotalBytes <= TransferredBytes) return string.Empty;

                var seconds = (TotalBytes - TransferredBytes) / BytesPerSecond;
                if (seconds < 60) return $"残り {seconds:F0} 秒";
                if (seconds < 3600) return $"残り {seconds / 60:F0} 分";
                return $"残り {seconds / 3600:F1} 時間";
            }
        }

        public string DetailText =>
            string.Join("  ", new[] { OwnerName.Length > 0 ? (IsUpload ? string.Empty : $"{OwnerName} さんから") : string.Empty, SpeedText, RemainingText }
                .Where(text => !string.IsNullOrEmpty(text)));

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}
