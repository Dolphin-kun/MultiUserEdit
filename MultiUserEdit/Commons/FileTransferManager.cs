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
        private const int ChunkSize = 1024 * 1024;
        private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(3);

        private static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(15);

        private static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromMinutes(5);

        private class FileTask
        {
            public string FileName { get; set; } = string.Empty;
            public Guid TransferId { get; set; }
            public long TotalBytes { get; set; }
            public long TransferredBytes { get; set; }
            public string OwnerName { get; set; } = string.Empty;

            private DateTime sampledAt = DateTime.UtcNow;
            private long sampledBytes;
            private double speed;

            public double BytesPerSecond
            {
                get
                {
                    var now = DateTime.UtcNow;
                    var elapsed = (now - sampledAt).TotalSeconds;
                    if (elapsed < 0.5) return speed;

                    var delta = TransferredBytes - sampledBytes;
                    var sample = delta / elapsed;
                    speed = speed <= 0 ? sample : speed * 0.6 + sample * 0.4;

                    sampledAt = now;
                    sampledBytes = TransferredBytes;
                    return speed;
                }
            }
        }

        public Func<Guid, string>? GetUserName { get; set; }

        public Func<bool>? CanShareFiles { get; set; }

        private class Announcement
        {
            public required string FilePath { get; init; }
            public required string FileName { get; init; }
            public required int ExpectedReplies { get; init; }
            public DateTime AnnouncedAt { get; } = DateTime.UtcNow;

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

        public Func<int>? GetPeerCount { get; set; }

        private readonly ConcurrentDictionary<string, FileTask> activeUploads = new();
        private readonly ConcurrentDictionary<Guid, IncomingTransfer> incomingTransfers = new();
        private readonly ConcurrentDictionary<Guid, FileTask> activeDownloads = new();

        private readonly ConcurrentDictionary<Guid, Announcement> announcements = new();
        private readonly ConcurrentDictionary<string, byte> announcingPaths = new();
        private readonly ConcurrentDictionary<string, (DateTime WriteTime, long Size, string Hash)> hashCache = new();
        private readonly ConcurrentDictionary<string, bool> sendDecisions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (DateTime WriteTime, long Size)> announcedFiles = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string, string>? TransferCompleted;
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

        private static readonly ConcurrentDictionary<string, byte> receivedFiles = new(StringComparer.OrdinalIgnoreCase);

        public static void CleanUpTempFiles()
        {
            if (MultiUserEditSettings.Default.StorageMode != FileStorageMode.TemporarySession)
            {
                return;
            }

            foreach (var path in receivedFiles.Keys)
            {
                try { File.Delete(path); } catch { }
            }

            receivedFiles.Clear();
            RemoveEmptyDirectories();
        }

        private static void RemoveEmptyDirectories()
        {
            try
            {
                var dir = GetSaveDirectory();
                if (!Directory.Exists(dir)) return;

                foreach (var subDirectory in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(subDirectory).Length == 0) Directory.Delete(subDirectory);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Temp cleanup failed: {ex.Message}");
            }
        }

        public static void DeleteAllFiles()
        {
            receivedFiles.Clear();

            try
            {
                var dir = GetSaveDirectory();
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { }
                    }

                    foreach (var subDirectory in Directory.GetDirectories(dir))
                    {
                        try { Directory.Delete(subDirectory, recursive: true); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Temp cleanup failed: {ex.Message}");
            }
        }

        private static readonly TimeSpan ConfirmBatchWindow = TimeSpan.FromMilliseconds(200);

        private readonly System.Threading.Lock confirmLock = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> pendingConfirms = new(StringComparer.OrdinalIgnoreCase);
        private bool confirmFlushScheduled;

        public Task<bool> ConfirmSendAsync(string filePath)
        {
            if (!File.Exists(filePath)) return Task.FromResult(false);

            if (sendDecisions.TryGetValue(filePath, out var remembered)) return Task.FromResult(remembered);

            if (CanShareFiles?.Invoke() == false)
            {
                sendDecisions[filePath] = false;
                ErrorNotifier.NotifyOnce(
                    "素材を送信できませんでした",
                    "ホストの設定により、この部屋では素材ファイルを送信できません。");
                return Task.FromResult(false);
            }

            if (!MultiUserEditSettings.Default.IsExtensionAllowed(filePath))
            {
                sendDecisions[filePath] = false;
                ErrorNotifier.NotifyOnce(
                    "素材を送信できませんでした",
                    $"拡張子 [{Path.GetExtension(filePath)}] のファイルは共有設定で許可されていないため送信できません。\n" +
                    "設定 → ファイル → 共有可能なファイル形式 から許可してください。");
                return Task.FromResult(false);
            }

            if (!MultiUserEditSettings.Default.ConfirmBeforeFileSend)
            {
                sendDecisions[filePath] = true;
                return Task.FromResult(true);
            }

            lock (confirmLock)
            {
                if (sendDecisions.TryGetValue(filePath, out remembered)) return Task.FromResult(remembered);
                if (pendingConfirms.TryGetValue(filePath, out var existing)) return existing.Task;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingConfirms[filePath] = waiter;

                if (!confirmFlushScheduled)
                {
                    confirmFlushScheduled = true;
                    _ = Task.Delay(ConfirmBatchWindow).ContinueWith(_ => FlushConfirms(), TaskScheduler.Default);
                }

                return waiter.Task;
            }
        }

        private void FlushConfirms()
        {
            List<KeyValuePair<string, TaskCompletionSource<bool>>> batch;
            lock (confirmLock)
            {
                confirmFlushScheduled = false;
                batch = [.. pendingConfirms];
                pendingConfirms.Clear();
            }

            if (batch.Count == 0) return;

            var confirmed = AskUser([.. batch.Select(entry => Path.GetFileName(entry.Key)).Distinct(StringComparer.OrdinalIgnoreCase)]);

            foreach (var entry in batch)
            {
                sendDecisions[entry.Key] = confirmed;
                entry.Value.TrySetResult(confirmed);
            }
        }

        private static bool AskUser(IReadOnlyList<string> fileNames)
        {
            if (fileNames.Count == 0) return false;

            const int MaxListed = 10;
            var message = fileNames.Count == 1
                ? $"素材ファイル「{fileNames[0]}」を他メンバーに送信してもよろしいですか？"
                : $"以下の素材ファイル {fileNames.Count} 件を他メンバーに送信してもよろしいですか？\n\n"
                    + string.Join("\n", fileNames.Take(MaxListed).Select(name => "・" + name))
                    + (fileNames.Count > MaxListed ? $"\n ほか {fileNames.Count - MaxListed} 件" : string.Empty);

            var confirmed = false;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                confirmed = System.Windows.MessageBox.Show(
                    message,
                    "ファイル送信の確認",
                    System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes;
            });

            return confirmed;
        }

        public bool IsSendDenied(string filePath) =>
            sendDecisions.TryGetValue(filePath, out var allowed) && !allowed;

        public void MarkSendAllowed(string filePath) => sendDecisions.TryAdd(filePath, true);

        public bool IsSendAllowed(string filePath) =>
            sendDecisions.TryGetValue(filePath, out var allowed) && allowed;

        public async Task SendFileAsync(string filePath, SessionClient sessionClient, Guid executorId)
        {
            if (!await ConfirmSendAsync(filePath)) return;
            await TransferAsync(filePath, sessionClient, executorId);
        }

        internal Task TransferAsync(string filePath, SessionClient sessionClient, Guid executorId) =>
            TransferAsync(new SharedFile(filePath, Path.GetFileName(filePath)), null, sessionClient, executorId);

        internal Task TransferAsync(SharedFile file, string? characterName, SessionClient sessionClient, Guid executorId) =>
            Task.Run(() => TransferCoreAsync(file, characterName, sessionClient, executorId));

        private async Task TransferCoreAsync(SharedFile file, string? characterName, SessionClient sessionClient, Guid executorId)
        {
            var filePath = file.FullPath;
            var fileName = file.Name;
            if (string.IsNullOrEmpty(fileName)) return;
            if (!File.Exists(filePath)) return;

            if (activeUploads.ContainsKey(filePath)) return;
            if (IsAlreadyAnnounced(filePath)) return;
            if (!announcingPaths.TryAdd(filePath, 0)) return;

            Guid transferId;
            Announcement announcement;

            try
            {
                var hash = await ComputeHashAsync(filePath);
                if (hash == null) return;

                var peerCount = Math.Max(0, GetPeerCount?.Invoke() ?? 0);
                if (peerCount == 0) return;

                transferId = Guid.NewGuid();
                announcement = new Announcement
                {
                    FilePath = filePath,
                    FileName = fileName,
                    ExpectedReplies = peerCount
                };
                announcements[transferId] = announcement;
                PruneAnnouncements();

                var availableEvt = new FileAvailableEvent(transferId, fileName, new FileInfo(filePath).Length, hash, characterName)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = executorId
                };
                await sessionClient.SendAsync(null, availableEvt);

                await Task.WhenAny(announcement.AllReplied.Task, Task.Delay(RequestWindow));
            }
            finally
            {
                announcingPaths.TryRemove(filePath, out _);
                RememberAnnounced(filePath);
            }

            var requesters = announcement.Requesters.Keys.ToList();

            if (requesters.Count == 0)
            {
                if (announcement.Replies.IsEmpty)
                {
                    Debug.WriteLine($"[MultiUserEdit] No reply within the request window, sending to everyone: {fileName}");
                    await SendChunksAsync(filePath, fileName, transferId, sessionClient, executorId, null);
                    return;
                }

                Debug.WriteLine($"[MultiUserEdit] Skipped transfer (all peers already have it): {fileName}");
                RecordSent(filePath, wasTransferred: false);
                return;
            }

            await SendChunksAsync(filePath, fileName, transferId, sessionClient, executorId,
                [.. requesters.Select(id => id.ToString())]);
        }

        internal async Task SendDirectAsync(SharedFile file, Guid targetUserId, SessionClient sessionClient, Guid executorId)
        {
            if (string.IsNullOrEmpty(file.Name) || !File.Exists(file.FullPath)) return;

            await SendChunksAsync(file.FullPath, file.Name, Guid.NewGuid(),
                sessionClient, executorId, [targetUserId.ToString()]);
        }

        private bool IsAlreadyAnnounced(string filePath)
        {
            var info = new FileInfo(filePath);

            if (announcedFiles.TryGetValue(filePath, out var announced)
                && announced.WriteTime == info.LastWriteTimeUtc
                && announced.Size == info.Length)
            {
                return true;
            }

            var key = SharedFileKey(Path.GetFileName(filePath), info.Length);
            return sharedFileKeys.ContainsKey(key);
        }

        private static string SharedFileKey(string fileName, long size) => $"{fileName}|{size}";

        private readonly ConcurrentDictionary<string, byte> sharedFileKeys = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, byte> cancelledTargets = new();

        private static string CancelKey(Guid transferId, Guid userId) => $"{transferId}|{userId}";

        public void NoteCancelledByPeer(Guid transferId, Guid userId) =>
            cancelledTargets.TryAdd(CancelKey(transferId, userId), 0);

        public Guid CancelIncoming(Guid transferId)
        {
            if (!incomingTransfers.TryRemove(transferId, out var transfer)) return Guid.Empty;

            activeDownloads.TryRemove(transferId, out _);
            transfer.Abort();
            NotifySummary();

            return transfer.SenderId;
        }

        internal void NoteSharedFile(string fileName, long size) =>
            sharedFileKeys.TryAdd(SharedFileKey(fileName, size), 0);

        private static readonly ConcurrentDictionary<string, string> fileOwners = new(StringComparer.OrdinalIgnoreCase);

        public static string GetOwnerName(string path) =>
            fileOwners.TryGetValue(path, out var name) ? name : string.Empty;

        private static void RememberOwner(string path, string ownerName)
        {
            if (!string.IsNullOrEmpty(ownerName)) fileOwners[path] = ownerName;
        }

        private void RememberAnnounced(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                announcedFiles[filePath] = (info.LastWriteTimeUtc, info.Length);
                NoteSharedFile(Path.GetFileName(filePath), info.Length);
            }
            catch { }
        }

        public void ForgetAnnouncedFiles()
        {
            announcedFiles.Clear();
            sharedFileKeys.Clear();
        }

        private static void NotifyBlocked(string fileName)
        {
            ErrorNotifier.NotifyOnce(
                "素材を受け取れませんでした",
                $"拡張子 [{Path.GetExtension(fileName)}] のファイルは共有設定で許可されていないため受け取れません。\n" +
                "設定 → ファイル → 共有可能なファイル形式 から許可してください。");
        }

        internal async Task HandleFileRequestAsync(FileRequestEvent evt, SessionClient sessionClient, Guid executorId)
        {
            if (!announcements.TryGetValue(evt.TransferId, out var announcement)) return;

            if (announcingPaths.ContainsKey(announcement.FilePath))
            {
                announcement.AddReply(evt.RequesterId, evt.NeedsTransfer);
                return;
            }

            if (!evt.NeedsTransfer) return;

            await SendChunksAsync(announcement.FilePath, announcement.FileName, evt.TransferId,
                sessionClient, executorId, [evt.RequesterId.ToString()]);
        }

        private static Task SendToTargetsAsync(SessionClient sessionClient, IReadOnlyList<string>? targetIds, object data) =>
            targetIds == null ? sessionClient.SendAsync(null, data) : sessionClient.SendToManyAsync(targetIds, data);

        private Task SendChunksAsync(string filePath, string fileName, Guid transferId,
            SessionClient sessionClient, Guid executorId, IReadOnlyList<string>? targetIds) =>
            Task.Run(() => SendChunksCoreAsync(filePath, fileName, transferId, sessionClient, executorId, targetIds));

        private async Task SendChunksCoreAsync(string filePath, string fileName, Guid transferId,
            SessionClient sessionClient, Guid executorId, IReadOnlyList<string>? targetIds)
        {
            if (!File.Exists(filePath)) return;

            var fileInfo = new FileInfo(filePath);
            var fileTask = new FileTask
            {
                FileName = fileName,
                TransferId = transferId,
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
                await SendToTargetsAsync(sessionClient, targetIds, startEvt);

                var buffer = new byte[ChunkSize];
                await using var stream = OpenForSequentialReadAsync(filePath);

                var targetGuids = targetIds?
                    .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
                    .ToArray();

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    if (IsCancelledByEveryone(transferId, targetGuids))
                    {
                        Debug.WriteLine($"[MultiUserEdit] Every receiver cancelled: {fileName}");
                        return;
                    }

                    var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (read <= 0) break;

                    var chunkEvt = new FileChunkEvent(transferId, chunkIndex, totalChunks, Convert.ToBase64String(buffer, 0, read))
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = executorId
                    };
                    await SendToTargetsAsync(sessionClient, targetIds, chunkEvt);

                    fileTask.TransferredBytes = Math.Min(fileTask.TotalBytes, fileTask.TransferredBytes + read);
                    NotifyProgress();
                }

                RecordSent(filePath, wasTransferred: true);
            }
            finally
            {
                activeUploads.TryRemove(filePath, out _);
                NotifySummary();
            }
        }

        private readonly ConcurrentDictionary<string, SentFileRecord> sentFiles = new(StringComparer.OrdinalIgnoreCase);

        public event Action? SentFilesChanged;

        public IReadOnlyList<SentFileRecord> GetSentFiles() => [.. sentFiles.Values.OrderByDescending(record => record.SentAt)];

        private void RecordSent(string filePath, bool wasTransferred)
        {
            try
            {
                var size = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
                sentFiles.AddOrUpdate(
                    filePath,
                    _ => new SentFileRecord(filePath, size, DateTime.Now, wasTransferred),
                    (_, existing) => new SentFileRecord(filePath, size, DateTime.Now, wasTransferred || existing.WasTransferred));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Record sent file failed: {ex.Message}");
                return;
            }

            SentFilesChanged?.Invoke();
        }

        internal async Task<bool> NeedsTransferAsync(FileAvailableEvent evt)
        {
            if (!MultiUserEditSettings.Default.IsExtensionAllowed(evt.FileName))
            {
                Debug.WriteLine($"[MultiUserEdit] Security Block: Rejected file announcement with disallowed extension: {evt.FileName}");
                NotifyBlocked(evt.FileName);
                return false;
            }

            var characterPath = TachieFileResolver.ResolveLocalPath(
                TachieFileResolver.GetBaseDirectory(evt.CharacterName), evt.FileName);

            foreach (var localPath in new[] { MediaFileResolver.ResolveLocalTempPath(evt.FileName), characterPath })
            {
                if (string.IsNullOrEmpty(localPath)) continue;

                if (!File.Exists(localPath) || new FileInfo(localPath).Length != evt.FileSize) continue;

                Debug.WriteLine($"[MultiUserEdit] Reused local file (same name and size): {localPath}");
                TransferCompleted?.Invoke(evt.TransferId.ToString(), localPath);
                return false;
            }

            return await ConfirmReceiveAsync(evt);
        }

        private readonly ConcurrentDictionary<string, bool> receiveDecisions = new(StringComparer.OrdinalIgnoreCase);

        private Task<bool> ConfirmReceiveAsync(FileAvailableEvent evt)
        {
            var settings = MultiUserEditSettings.Default;
            if (!settings.ConfirmLargeFileReceive) return Task.FromResult(true);

            var threshold = Math.Max(1, settings.LargeFileConfirmMegaBytes) * 1024L * 1024L;
            if (evt.FileSize < threshold) return Task.FromResult(true);

            var key = SharedFileKey(evt.FileName, evt.FileSize);
            if (receiveDecisions.TryGetValue(key, out var remembered)) return Task.FromResult(remembered);

            var accepted = AskReceive(evt.FileName, evt.FileSize, GetUserName?.Invoke(evt.ExecutorId) ?? string.Empty);
            receiveDecisions[key] = accepted;

            return Task.FromResult(accepted);
        }

        private static bool AskReceive(string fileName, long fileSize, string ownerName)
        {
            var sender = string.IsNullOrEmpty(ownerName) ? "ほかの参加者" : $"{ownerName} さん";
            var message = $"{sender} から、サイズの大きい素材ファイルが届こうとしています。\n\n"
                + $"ファイル名: {Path.GetFileName(fileName)}\n"
                + $"サイズ: {FormatSize(fileSize)}\n\n"
                + "受け取りますか？\n"
                + "「いいえ」を選ぶと、このファイルは受信しません。";

            var accepted = false;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                accepted = System.Windows.MessageBox.Show(
                    message,
                    "大きい素材ファイルの受け取り",
                    System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes;
            });

            return accepted;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / 1024.0:F0} KB";
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

                await using var stream = OpenForSequentialReadAsync(filePath);
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

        public void HandleTransferStart(FileTransferStartEvent evt)
        {
            if (!MultiUserEditSettings.Default.IsExtensionAllowed(evt.FileName))
            {
                Debug.WriteLine($"[MultiUserEdit] Security Block: Rejected file transfer with disallowed extension: {evt.FileName}");
                NotifyBlocked(evt.FileName);
                return;
            }

            if (receiveDecisions.TryGetValue(SharedFileKey(evt.FileName, evt.FileSize), out var accepted) && !accepted)
            {
                Debug.WriteLine($"[MultiUserEdit] Declined earlier, not receiving: {evt.FileName}");
                return;
            }

            var finalPath = MediaFileResolver.ResolveLocalTempPath(evt.FileName);
            var tempPath = Path.Combine(GetSaveDirectory(), $"{evt.TransferId}.tmp");

            var chunkSize = evt.ChunkSize > 0 ? evt.ChunkSize : ChunkSize;

            incomingTransfers[evt.TransferId] = new IncomingTransfer(evt.FileName, finalPath, tempPath, evt.TotalChunks, chunkSize)
            {
                SenderId = evt.ExecutorId
            };
            activeDownloads[evt.TransferId] = new FileTask
            {
                FileName = evt.FileName,
                TransferId = evt.TransferId,
                TotalBytes = evt.FileSize,
                TransferredBytes = 0,
                OwnerName = GetUserName?.Invoke(evt.ExecutorId) ?? string.Empty
            };

            NotifySummary();
        }

        public void HandleChunk(FileChunkEvent evt)
        {
            if (!incomingTransfers.TryGetValue(evt.TransferId, out var transfer)) return;

            if (DateTime.UtcNow - transfer.LastActivityAt > TransferTimeout)
            {
                AbortTransfer(evt.TransferId, transfer);
                return;
            }

            transfer.LastActivityAt = DateTime.UtcNow;

            byte[] chunkBytes;
            try
            {
                chunkBytes = Convert.FromBase64String(evt.Data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] FileTransfer decode failed: {ex.Message}");
                AbortTransfer(evt.TransferId, transfer);
                return;
            }

            var transferId = evt.TransferId;
            var chunkIndex = evt.ChunkIndex;

            transfer.QueueWrite(() => WriteChunk(transferId, transfer, chunkIndex, chunkBytes));
        }

        private void WriteChunk(Guid transferId, IncomingTransfer transfer, int chunkIndex, byte[] chunkBytes)
        {
            try
            {
                if (!transfer.WriteChunk(chunkIndex, chunkBytes, chunkBytes.Length)) return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] FileTransfer write failed: {ex.Message}");
                AbortTransfer(transferId, transfer);
                return;
            }

            transfer.ReceivedChunks++;

            if (activeDownloads.TryGetValue(transferId, out var downTask))
            {
                downTask.TransferredBytes = Math.Min(downTask.TotalBytes, downTask.TransferredBytes + chunkBytes.Length);
            }
            NotifyProgress();

            if (transfer.ReceivedChunks < transfer.TotalChunks) return;

            FinishTransfer(transferId, transfer);
        }

        private void FinishTransfer(Guid transferId, IncomingTransfer transfer)
        {
            var ownerName = activeDownloads.TryGetValue(transferId, out var task) ? task.OwnerName : string.Empty;

            incomingTransfers.TryRemove(transferId, out _);
            activeDownloads.TryRemove(transferId, out _);
            NotifySummary();

            try
            {
                transfer.CloseStream();

                var saveDirectory = Path.GetDirectoryName(transfer.SavePath);
                if (!string.IsNullOrEmpty(saveDirectory)) Directory.CreateDirectory(saveDirectory);

                File.Move(transfer.TempPath, transfer.SavePath, overwrite: true);
                receivedFiles[transfer.SavePath] = 0;
                NoteSharedFile(Path.GetFileName(transfer.SavePath), new FileInfo(transfer.SavePath).Length);
                RememberOwner(transfer.SavePath, ownerName);

                TransferCompleted?.Invoke(transferId.ToString(), transfer.SavePath);
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

            transfer.Abort();

            NotifySummary();
        }

        private bool IsCancelledByEveryone(Guid transferId, Guid[]? targetGuids)
        {
            if (targetGuids == null || targetGuids.Length == 0) return false;

            return targetGuids.All(id => cancelledTargets.ContainsKey(CancelKey(transferId, id)));
        }

        private static FileStream OpenForSequentialReadAsync(string filePath) =>
            new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        private static readonly TimeSpan ProgressNotifyInterval = TimeSpan.FromMilliseconds(200);

        private long lastProgressNotifiedTicks;

        private void NotifyProgress()
        {
            var now = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref lastProgressNotifiedTicks);

            if (now - previous < ProgressNotifyInterval.Ticks) return;
            if (Interlocked.CompareExchange(ref lastProgressNotifiedTicks, now, previous) != previous) return;

            NotifySummary();
        }

        private void NotifySummary()
        {
            var uploads = activeUploads.Values.ToList();
            var downloads = activeDownloads.ToList();

            if (uploads.Count == 0 && downloads.Count == 0)
            {
                TransferSummaryChanged?.Invoke(new TransferSummary());
                return;
            }

            var allTransfers = uploads.Concat(downloads.Select(pair => pair.Value)).ToList();
            long totalBytes = allTransfers.Sum(t => t.TotalBytes);
            long transferredBytes = allTransfers.Sum(t => t.TransferredBytes);
            double progress = totalBytes > 0 ? (double)transferredBytes / totalBytes * 100.0 : 0.0;

            var rawName = allTransfers.FirstOrDefault()?.FileName ?? string.Empty;
            var currentFile = string.IsNullOrEmpty(rawName) ? string.Empty : Path.GetFileName(rawName.Replace('/', Path.DirectorySeparatorChar));

            var summary = new TransferSummary
            {
                UploadCount = uploads.Count,
                DownloadCount = downloads.Count,
                CurrentFileName = currentFile,
                OverallProgress = Math.Clamp(progress, 0.0, 100.0),
                Items =
                [
                    .. uploads.Select(t => new TransferItemInfo
                    {
                        TransferId = t.TransferId,
                        Name = t.FileName,
                        IsUpload = true,
                        BytesPerSecond = t.BytesPerSecond,
                        OwnerName = t.OwnerName,
                        TotalBytes = t.TotalBytes,
                        TransferredBytes = t.TransferredBytes
                    }),
                    .. downloads.Select(pair => new TransferItemInfo
                    {
                        TransferId = pair.Key,
                        Name = pair.Value.FileName,
                        IsUpload = false,
                        BytesPerSecond = pair.Value.BytesPerSecond,
                        OwnerName = pair.Value.OwnerName,
                        TotalBytes = pair.Value.TotalBytes,
                        TransferredBytes = pair.Value.TransferredBytes
                    })
                ]
            };

            TransferSummaryChanged?.Invoke(summary);
        }

        public bool IsReceivingAny()
        {
            var now = DateTime.UtcNow;
            return incomingTransfers.Values.Any(transfer => now - transfer.LastActivityAt <= TransferTimeout);
        }

        public void CancelAll()
        {
            foreach (var transfer in incomingTransfers.Values) transfer.Abort();

            incomingTransfers.Clear();
            activeUploads.Clear();
            activeDownloads.Clear();
            announcements.Clear();
            announcingPaths.Clear();
            cancelledTargets.Clear();
            NotifySummary();
        }
    }
}
