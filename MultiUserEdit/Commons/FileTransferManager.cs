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
        // Durable ObjectsのWebSocket受信上限は32MiB。Base64化で約1.33倍に膨らむため、
        // 8MB → 約10.7MB と余裕を持たせつつ、メッセージ数（＝Workerのリクエスト数）を抑える。
        private const int ChunkSize = 8 * 1024 * 1024;
        private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(3);

        // 告知してから「送ってほしい」の返答を待つ時間。この間に誰からも要求が来なければ転送しない。
        private static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(2);

        // 告知したファイルを覚えておく時間（ウィンドウ経過後に届いた要求にも応えられるようにする）
        private static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromMinutes(5);

        private class FileTask
        {
            public string FileName { get; set; } = string.Empty;
            public long TotalBytes { get; set; }
            public long TransferredBytes { get; set; }
        }

        // 告知済みファイル。要求が来たときに何を送ればよいか引くために保持する。
        private class Announcement
        {
            public required string FilePath { get; init; }
            public required string FileName { get; init; }
            public required int ExpectedReplies { get; init; }
            public DateTime AnnouncedAt { get; } = DateTime.UtcNow;

            // 全員から返事が揃ったら待たずに進むための合図
            public TaskCompletionSource AllReplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ConcurrentDictionary<Guid, byte> Replies { get; } = new();
            public ConcurrentDictionary<Guid, byte> Requesters { get; } = new();

            public void AddReply(Guid userId, bool needsTransfer)
            {
                if (needsTransfer) Requesters.TryAdd(userId, 0);

                Replies.TryAdd(userId, 0);
                if (Replies.Count >= ExpectedReplies) AllReplied.TrySetResult();
            }
        }

        // 返事を待つ相手の人数（自分以外の参加者数）を取得する
        public Func<int>? GetPeerCount { get; set; }

        // 送信管理と受信管理（インスタンスごとに独立）
        private readonly ConcurrentDictionary<string, FileTask> activeUploads = new();
        private readonly ConcurrentDictionary<Guid, IncomingTransfer> incomingTransfers = new();
        private readonly ConcurrentDictionary<Guid, FileTask> activeDownloads = new();

        private readonly ConcurrentDictionary<Guid, Announcement> announcements = new();
        // 告知の返答受付中のファイルパス（同じファイルの二重告知を防ぐ）
        private readonly ConcurrentDictionary<string, byte> announcingPaths = new();
        // ハッシュ計算のキャッシュ。パス・更新日時・サイズが同じなら再計算しない。
        private readonly ConcurrentDictionary<string, (DateTime WriteTime, long Size, string Hash)> hashCache = new();

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

            // 同じファイルの告知・送信が二重に走らないようにする
            if (activeUploads.ContainsKey(filePath)) return;
            if (!announcingPaths.TryAdd(filePath, 0)) return;

            Guid transferId;
            Announcement announcement;

            try
            {
                var hash = await ComputeHashAsync(filePath);
                if (hash == null) return;

                var peerCount = Math.Max(0, GetPeerCount?.Invoke() ?? 0);
                if (peerCount == 0) return; // 自分しかいないので送る相手がいない

                transferId = Guid.NewGuid();
                announcement = new Announcement
                {
                    FilePath = filePath,
                    FileName = fileName,
                    ExpectedReplies = peerCount
                };
                announcements[transferId] = announcement;
                PruneAnnouncements();

                var availableEvt = new FileAvailableEvent(transferId, fileName, new FileInfo(filePath).Length, hash)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = executorId
                };
                await sessionClient.SendAsync(null, availableEvt);

                // 全員の返事が揃えば即座に進む。返事が来ない相手がいる場合だけタイムアウトまで待つ。
                await Task.WhenAny(announcement.AllReplied.Task, Task.Delay(RequestWindow));
            }
            finally
            {
                announcingPaths.TryRemove(filePath, out _);
            }

            var requesters = announcement.Requesters.Keys.ToList();

            // 全員が同じ内容のファイルを既に持っている場合は1バイトも送らない
            if (requesters.Count == 0)
            {
                Debug.WriteLine($"[MultiUserEdit] Skipped transfer (all peers already have it): {fileName}");
                return;
            }

            // 要求者が1人だけならその人にだけ送る。複数なら全員へ配る方が総送信量は少ない。
            var targetId = requesters.Count == 1 ? requesters[0].ToString() : null;
            await SendChunksAsync(filePath, fileName, transferId, sessionClient, executorId, targetId);
        }

        /// <summary>ウィンドウ経過後に届いた要求。その人にだけ改めて送る。</summary>
        internal async Task HandleFileRequestAsync(FileRequestEvent evt, SessionClient sessionClient, Guid executorId)
        {
            if (!announcements.TryGetValue(evt.TransferId, out var announcement)) return;

            // 受付中なら記録するだけ。まとめて送るかどうかはTransferAsync側が判断する。
            if (announcingPaths.ContainsKey(announcement.FilePath))
            {
                announcement.AddReply(evt.RequesterId, evt.NeedsTransfer);
                return;
            }

            if (!evt.NeedsTransfer) return;

            await SendChunksAsync(announcement.FilePath, announcement.FileName, evt.TransferId,
                sessionClient, executorId, evt.RequesterId.ToString());
        }

        private async Task SendChunksAsync(string filePath, string fileName, Guid transferId,
            SessionClient sessionClient, Guid executorId, string? targetId)
        {
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
                var fileSize = fileInfo.Length;
                var totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize);

                var startEvt = new FileTransferStartEvent(transferId, fileName, fileSize, totalChunks, ChunkSize)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = executorId
                };
                await sessionClient.SendAsync(targetId, startEvt);

                // ファイル全体をメモリに載せず、1チャンクずつ読みながら送る。
                // チャンクが大きいため、まとめ送りはせず1つずつ送信する（同時に保持するのは1チャンク分だけ）。
                var buffer = new byte[ChunkSize];
                await using var stream = File.OpenRead(filePath);

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (read <= 0) break;

                    var chunkEvt = new FileChunkEvent(transferId, chunkIndex, totalChunks, Convert.ToBase64String(buffer, 0, read))
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = executorId
                    };
                    await sessionClient.SendAsync(targetId, chunkEvt);

                    fileTask.TransferredBytes = Math.Min(fileTask.TotalBytes, fileTask.TransferredBytes + read);
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

        /// <summary>
        /// 告知されたファイルを既に持っているか調べる。持っていれば転送を要求せず、
        /// ローカルのファイルをそのまま使ったことにして完了通知だけ出す。
        /// </summary>
        /// <returns>転送を要求する必要があるか</returns>
        internal async Task<bool> NeedsTransferAsync(FileAvailableEvent evt)
        {
            if (!MultiUserEditSettings.Default.IsExtensionAllowed(evt.FileName))
            {
                Debug.WriteLine($"[MultiUserEdit] Security Block: Rejected file announcement with disallowed extension: {evt.FileName}");
                return false;
            }

            var savePath = Path.Combine(GetSaveDirectory(), evt.FileName);

            // サイズが違えば内容も違うので、ハッシュ計算をせずに要求する
            if (!File.Exists(savePath) || new FileInfo(savePath).Length != evt.FileSize) return true;

            var localHash = await ComputeHashAsync(savePath);
            if (localHash != evt.Hash) return true;

            // アイテム側は転送完了を待っているので、届いたことにして先へ進めてやる
            Debug.WriteLine($"[MultiUserEdit] Reused local file (same content): {evt.FileName}");
            TransferCompleted?.Invoke(evt.TransferId.ToString(), savePath);
            return false;
        }

        private async Task<string?> ComputeHashAsync(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return null;

                if (hashCache.TryGetValue(filePath, out var cached) &&
                    cached.WriteTime == info.LastWriteTimeUtc && cached.Size == info.Length)
                {
                    return cached.Hash;
                }

                // 全体をメモリに載せずに済むようストリームで計算する
                await using var stream = File.OpenRead(filePath);
                var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
                var hash = Convert.ToHexStringLower(hashBytes);

                hashCache[filePath] = (info.LastWriteTimeUtc, info.Length, hash);
                return hash;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Hash failed for {filePath}: {ex.Message}");
                return null;
            }
        }

        private void PruneAnnouncements()
        {
            var expired = announcements
                .Where(pair => DateTime.UtcNow - pair.Value.AnnouncedAt > AnnouncementLifetime)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var transferId in expired) announcements.TryRemove(transferId, out _);
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

            // 書き込み位置は送信側のチャンクサイズ基準で決める（未指定なら自分と同じ設定とみなす）
            var chunkSize = evt.ChunkSize > 0 ? evt.ChunkSize : ChunkSize;

            incomingTransfers[evt.TransferId] = new IncomingTransfer(evt.FileName, finalPath, tempPath, evt.TotalChunks, chunkSize);
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
                AbortTransfer(evt.TransferId, transfer);
                return;
            }

            int chunkLength;
            try
            {
                var chunkBytes = Convert.FromBase64String(evt.Data);
                chunkLength = chunkBytes.Length;

                // 溜め込まずにその場で一時ファイルへ書き出す（保持するのは1チャンク分だけ）
                var stream = transfer.OpenStream();
                stream.Position = (long)evt.ChunkIndex * transfer.ChunkSize;
                stream.Write(chunkBytes, 0, chunkBytes.Length);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] FileTransfer write failed: {ex.Message}");
                AbortTransfer(evt.TransferId, transfer);
                return;
            }

            transfer.ReceivedChunks++;

            if (activeDownloads.TryGetValue(evt.TransferId, out var downTask))
            {
                downTask.TransferredBytes = Math.Min(downTask.TotalBytes, downTask.TransferredBytes + chunkLength);
            }
            NotifySummary();

            if (transfer.ReceivedChunks < transfer.TotalChunks) return;

            incomingTransfers.TryRemove(evt.TransferId, out _);
            activeDownloads.TryRemove(evt.TransferId, out _);
            NotifySummary();

            try
            {
                transfer.CloseStream();

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

        private void AbortTransfer(Guid transferId, IncomingTransfer transfer)
        {
            incomingTransfers.TryRemove(transferId, out _);
            activeDownloads.TryRemove(transferId, out _);

            transfer.CloseStream();
            try { File.Delete(transfer.TempPath); } catch { }

            NotifySummary();
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
            // 書き込み中の一時ファイルを開きっぱなしにしないよう、必ず閉じてから捨てる
            foreach (var transfer in incomingTransfers.Values)
            {
                transfer.CloseStream();
                try { File.Delete(transfer.TempPath); } catch { }
            }

            incomingTransfers.Clear();
            activeUploads.Clear();
            activeDownloads.Clear();
            announcements.Clear();
            announcingPaths.Clear();
            NotifySummary();
        }
    }
}
