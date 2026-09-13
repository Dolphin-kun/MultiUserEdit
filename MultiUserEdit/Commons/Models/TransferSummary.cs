namespace MultiUserEdit.Commons.Models
{
    public class TransferSummary
    {
        public int UploadCount { get; set; }
        public int DownloadCount { get; set; }
        public string CurrentFileName { get; set; } = string.Empty;
        public double OverallProgress { get; set; }
        public IReadOnlyList<TransferItemInfo> Items { get; set; } = [];
        public bool IsActive => UploadCount > 0 || DownloadCount > 0;
        public bool IsUploading => UploadCount > 0;

        public string DisplayText
        {
            get
            {
                if (!IsActive) return string.Empty;

                if (UploadCount > 0 && DownloadCount > 0)
                {
                    return $"送信 {UploadCount}件 / 受信 {DownloadCount}件: {CurrentFileName}";
                }

                if (IsUploading)
                {
                    return UploadCount == 1
                        ? $"{CurrentFileName} を送信中"
                        : $"{CurrentFileName} ほか{UploadCount - 1}件を送信中";
                }

                return DownloadCount == 1
                    ? $"{CurrentFileName} をダウンロード中"
                    : $"{CurrentFileName} ほか{DownloadCount - 1}件をダウンロード中";
            }
        }
    }
}
