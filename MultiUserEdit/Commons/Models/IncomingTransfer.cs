namespace MultiUserEdit.Commons.Models
{
    public sealed class IncomingTransfer(string fileName, string savePath, string tempPath, int totalChunks)
    {
        public string FileName { get; } = fileName;
        public string SavePath { get; } = savePath;
        public string TempPath { get; } = tempPath;
        public int TotalChunks { get; } = totalChunks;
        public byte[][] Chunks { get; } = new byte[totalChunks][];
        public int ReceivedChunks { get; set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
    }
}
