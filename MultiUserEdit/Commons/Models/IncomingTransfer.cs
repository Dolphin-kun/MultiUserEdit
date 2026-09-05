using System.IO;

namespace MultiUserEdit.Commons.Models
{
    public sealed class IncomingTransfer(string fileName, string savePath, string tempPath, int totalChunks, int chunkSize)
    {
        public string FileName { get; } = fileName;
        public string SavePath { get; } = savePath;
        public string TempPath { get; } = tempPath;
        public int TotalChunks { get; } = totalChunks;
        public int ChunkSize { get; } = chunkSize;
        public int ReceivedChunks { get; set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        // 受信したチャンクはメモリに溜めず、そのまま一時ファイルへ書き出す
        // （チャンクが大きいため、全チャンクを保持するとファイルサイズ分のメモリを消費してしまう）。
        private FileStream? stream;

        public FileStream OpenStream()
        {
            return stream ??= new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }

        public void CloseStream()
        {
            stream?.Dispose();
            stream = null;
        }
    }
}
