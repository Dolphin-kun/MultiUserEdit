using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using MultiUserEdit.Settings;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons
{
    internal class FileTransferManager
    {
        private const int ChunkSize = 256 * 1024; // 256KB (高速化)
        private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(3);

        private class FileTask
        {
            public string FileName { get; set; } = string.Empty;
            public long TotalBytes { get; set; }
            public long TransferredBytes { get; set; }
        }

        // 送信管理と受信管理（インスタンスごとに独立）
        private readonly ConcurrentDictionary<string, FileTask> activeUploads = new();
        private readonly ConcurrentDictionary<Guid, IncomingTransfer> incomingTransfers = new();
        private readonly ConcurrentDictionary<Guid, FileTask> activeDownloads = new();

        public event Action<string, string>? TransferCompleted;
        // プロセス内・YMM4インスタンス間での干渉を防ぐためインスタンスイベントにする
        public event Action<TransferSummary>? TransferSummaryChanged;

        static FileTransferManager()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanUpTempFiles();
        }

        public static string GetSaveDirectory()
        {
            var baseTempDir = AppDirectories.TemporaryDirectory;
            var dir = Path.Combine(baseTempDir, "MultiUserEdit", "Media");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void CleanUpTempFiles()
        {
            if (MultiUserEditSettings.Default.StorageMode != FileStorageMode.TemporarySession)
            {
                return;
            }

            try
            {
                var dir = GetSaveDirectory();
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Temp cleanup failed: {ex.Message}");
            }
        }

        // 拡張子ブロック・送信確認ダイアログのみを行う（実際の転送は行わない）。
        // 呼び出し側がイベント送信前に「この転送は実際に行われるか」を判定するために使う。
        public bool ConfirmSend(string filePath)
        {
            if (!File.Exists(filePath)) return false;

            if (!MultiUserEditSettings.Default.IsExtensionAllowed(filePath))
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show($"拡張子 [{Path.GetExtension(filePath)}] のファイルは共有設定で許可されていないため送信できません。\n(設定画面から共有可能な拡張子を変更できます)", "送信ブロック", System.Windows.MessageBoxButton.OK);
                });
                return false;
            }

            if (MultiUserEditSettings.Default.ConfirmBeforeFileSend)
            {
                bool confirmed = false;
                var fileName = Path.GetFileName(filePath);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    var result = System.Windows.MessageBox.Show($"素材ファイル「{fileName}」を他メンバーに送信してもよろしいですか？", "ファイル送信の確認", System.Windows.MessageBoxButton.YesNo);
                    confirmed = (result == System.Windows.MessageBoxResult.Yes);
                });
                if (!confirmed) return false;
            }

            return true;
        }

        public async Task SendFileAsync(string filePath, SessionClient sessionClient, Guid executorId)
        {
            if (!ConfirmSend(filePath)) return;
            await TransferAsync(filePath, sessionClient, executorId);
        }

        // 送信確認済みのファイルをチャンク転送する。ConfirmSend を事前に済ませている呼び出し側は
        // ダイアログを再表示させないためこちらを直接使う。
        internal async Task TransferAsync(string filePath, SessionClient sessionClient, Guid executorId)
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName)) return;

            if (!File.Exists(filePath)) return;

            var fileInfo = new FileInfo(filePath);
            var fileTask = new FileTask
            {
                FileName = fileName,
                TotalBytes = fileInfo.Length,
                TransferredBytes = 0
            };

            if (!activeUploads.TryAdd(filePath, fileTask)) return;

            NotifySummary();

            try
            {
                var fileBytes = await File.ReadAllBytesAsync(filePath);
                var transferId = Guid.NewGuid();
                var totalChunks = (int)Math.Ceiling((double)fileBytes.Length / ChunkSize);

                var startEvt = new FileTransferStartEvent(transferId, fileName, fileBytes.Length, totalChunks)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = executorId
                };
                await sessionClient.SendAsync(null, startEvt);

                const int batchSize = 5;
                for (int i = 0; i < totalChunks; i += batchSize)
                {
                    var sendTasks = new List<Task>();
                    long batchBytesSent = 0;
                    for (int j = 0; j < batchSize && (i + j) < totalChunks; j++)
                    {
                        var chunkIndex = i + j;
                        var offset = chunkIndex * ChunkSize;
                        var length = Math.Min(ChunkSize, fileBytes.Length - offset);
                        var chunkData = Convert.ToBase64String(fileBytes, offset, length);

                        var chunkEvt = new FileChunkEvent(transferId, chunkIndex, totalChunks, chunkData)
                        {
                            DateTime = DateTime.UtcNow,
                            ExecutorId = executorId
                        };
                        sendTasks.Add(sessionClient.SendAsync(null, chunkEvt));
                        batchBytesSent += length;
                    }

                    await Task.WhenAll(sendTasks);

                    fileTask.TransferredBytes = Math.Min(fileTask.TotalBytes, fileTask.TransferredBytes + batchBytesSent);
                    NotifySummary();

                    // 他のシーク・編集パケットがあれば割り込ませ、無ければ直ちに最高速で送信再開
                    await Task.Yield();
                }
            }
            finally
            {
                activeUploads.TryRemove(filePath, out _);
                NotifySummary();
            }
        }

        public void HandleTransferStart(FileTransferStartEvent evt, Guid localUserId)
        {
            if (!MultiUserEditSettings.Default.IsExtensionAllowed(evt.FileName))
            {
                Debug.WriteLine($"[MultiUserEdit] Security Block: Rejected file transfer with disallowed extension: {evt.FileName}");
                return;
            }

            var finalPath = Path.Combine(GetSaveDirectory(), evt.FileName);
            // tempPathはTransferId基準にして、同名ファイルの転送が同時に走っても書き込み中のバッファが
            // 衝突しないようにする（同じ動画を複数アイテムへ同時に送った場合等）
            var tempPath = Path.Combine(GetSaveDirectory(), $"{evt.TransferId}.tmp");

            incomingTransfers[evt.TransferId] = new IncomingTransfer(evt.FileName, finalPath, tempPath, evt.TotalChunks);
            activeDownloads[evt.TransferId] = new FileTask
            {
                FileName = evt.FileName,
                TotalBytes = evt.FileSize,
                TransferredBytes = 0
            };

            NotifySummary();
        }

        public void HandleChunk(FileChunkEvent evt, Guid localUserId)
        {
            if (!incomingTransfers.TryGetValue(evt.TransferId, out var transfer)) return;

            if (DateTime.UtcNow - transfer.StartedAt > TransferTimeout)
            {
                incomingTransfers.TryRemove(evt.TransferId, out _);
                activeDownloads.TryRemove(evt.TransferId, out _);
                NotifySummary();
                return;
            }

            var chunkBytes = Convert.FromBase64String(evt.Data);
            transfer.Chunks[evt.ChunkIndex] = chunkBytes;
            transfer.ReceivedChunks++;

            if (activeDownloads.TryGetValue(evt.TransferId, out var downTask))
            {
                downTask.TransferredBytes = Math.Min(downTask.TotalBytes, downTask.TransferredBytes + chunkBytes.Length);
            }
            NotifySummary();

            if (transfer.ReceivedChunks < transfer.TotalChunks) return;

            incomingTransfers.TryRemove(evt.TransferId, out _);
            activeDownloads.TryRemove(evt.TransferId, out _);
            NotifySummary();

            try
            {
                using (var fs = File.Create(transfer.TempPath))
                {
                    foreach (var chunk in transfer.Chunks)
                    {
                        if (chunk != null) fs.Write(chunk, 0, chunk.Length);
                    }
                }

                // Delete→Moveの2段階だと、その間だけSavePathにファイルが存在しない瞬間ができてしまい、
                // ちょうどそのタイミングでYMM4がサムネイル再読み込みを行うとFileNotFoundExceptionになる
                // （アイテムを連続して動かしているとサムネイル再読み込みの頻度が上がり発生しやすくなる）。
                // overwrite:trueで置き換えれば、この隙間なくアトミックに入れ替えられる。
                File.Move(transfer.TempPath, transfer.SavePath, overwrite: true);

                TransferCompleted?.Invoke(evt.TransferId.ToString(), transfer.SavePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] FileTransfer save failed: {ex.Message}");
            }
        }

        private void NotifySummary()
        {
            var uploads = activeUploads.Values.ToList();
            var downloads = activeDownloads.Values.ToList();

            if (uploads.Count == 0 && downloads.Count == 0)
            {
                TransferSummaryChanged?.Invoke(new TransferSummary());
                return;
            }

            bool isUploading = uploads.Count > 0;
            var targetList = isUploading ? uploads : downloads;

            long totalBytes = targetList.Sum(t => t.TotalBytes);
            long transferredBytes = targetList.Sum(t => t.TransferredBytes);
            double progress = totalBytes > 0 ? (double)transferredBytes / totalBytes * 100.0 : 0.0;
            var currentFile = targetList.FirstOrDefault()?.FileName ?? string.Empty;

            var summary = new TransferSummary
            {
                UploadCount = uploads.Count,
                DownloadCount = downloads.Count,
                CurrentFileName = currentFile,
                OverallProgress = Math.Clamp(progress, 0.0, 100.0)
            };

            TransferSummaryChanged?.Invoke(summary);
        }

        public void CancelAll()
        {
            incomingTransfers.Clear();
            activeUploads.Clear();
            activeDownloads.Clear();
            NotifySummary();
        }
    }
}
