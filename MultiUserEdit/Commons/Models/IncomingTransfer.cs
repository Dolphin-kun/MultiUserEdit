using System.IO;

namespace MultiUserEdit.Commons.Models
{
    public sealed class IncomingTransfer(string fileName, string savePath, string tempPath, int totalChunks, int chunkSize)
    {
        private const int QueuedWriteLimit = 24;

        public string FileName { get; } = fileName;
        public string SavePath { get; } = savePath;
        public string TempPath { get; } = tempPath;
        public int TotalChunks { get; } = totalChunks;
        public int ChunkSize { get; } = chunkSize;
        public int ReceivedChunks { get; set; }
        public Guid SenderId { get; set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

        public bool IsAborted { get; private set; }

        private readonly System.Threading.Lock streamLock = new();
        private readonly System.Threading.Lock chainLock = new();
        private readonly SemaphoreSlim writeSlots = new(QueuedWriteLimit, QueuedWriteLimit);
        private Task writeChain = Task.CompletedTask;
        private FileStream? stream;

        public void QueueWrite(Action work)
        {
            writeSlots.Wait();

            lock (chainLock)
            {
                writeChain = writeChain.ContinueWith(
                    _ =>
                    {
                        try { work(); }
                        finally { writeSlots.Release(); }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        public bool WriteChunk(int chunkIndex, byte[] data, int length)
        {
            lock (streamLock)
            {
                if (IsAborted) return false;

                stream ??= new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.Position = (long)chunkIndex * ChunkSize;
                stream.Write(data, 0, length);
                return true;
            }
        }

        public void CloseStream()
        {
            lock (streamLock)
            {
                stream?.Dispose();
                stream = null;
            }
        }

        public void Abort()
        {
            lock (streamLock)
            {
                IsAborted = true;
                stream?.Dispose();
                stream = null;
            }

            try { File.Delete(TempPath); } catch { }
        }
    }
}
