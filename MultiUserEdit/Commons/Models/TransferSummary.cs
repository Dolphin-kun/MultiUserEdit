namespace MultiUserEdit.Commons.Models
{
    public class TransferSummary
    {
        public int UploadCount { get; set; }
        public int DownloadCount { get; set; }
        public string CurrentFileName { get; set; } = string.Empty;
        public double OverallProgress { get; set; }
        public bool IsActive => UploadCount > 0 || DownloadCount > 0;
        public bool IsUploading => UploadCount > 0;

        public string DisplayText
        {
            get
            {
                if (!IsActive) return string.Empty;

                if (IsUploading)
                {
                    return UploadCount == 1
                        ? $"送信中: {CurrentFileName}"
                        : $"ファイル送信中 ({UploadCount}件): {CurrentFileName}";
                }
                else
                {
                    return DownloadCount == 1
                        ? $"受信中: {CurrentFileName}"
                        : $"ファイル受信中 ({DownloadCount}件): {CurrentFileName}";
                }
            }
        }
    }
}
