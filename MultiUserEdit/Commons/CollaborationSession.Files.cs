using MultiUserEdit.Commons.Events;
using System.IO;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        internal void RequestMissingFiles(IReadOnlyList<string> fileNames)
        {
            if (!IsKeptRoom || fileNames.Count == 0) return;

            var now = DateTime.UtcNow;
            lock (wantedFiles)
            {
                foreach (var name in fileNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (wantedFiles.TryGetValue(name, out var askedAt) && now - askedAt < FileWantRetry) continue;

                    wantedFiles[name] = now;
                    pendingWants.Add(name);
                }

                if (pendingWants.Count == 0 || wantFlushScheduled) return;
                wantFlushScheduled = true;
            }

            _ = FlushWantedFilesAsync();
        }

        private async Task FlushWantedFilesAsync()
        {
            await Task.Delay(FileWantBatchDelay);

            while (true)
            {
                string[] names;
                lock (wantedFiles)
                {
                    names = [.. pendingWants.Take(MaxFilesPerRequest)];
                    foreach (var name in names) pendingWants.Remove(name);

                    if (names.Length == 0)
                    {
                        wantFlushScheduled = false;
                        return;
                    }
                }

                names = [.. names.Where(name => !HasOrIsReceiving(name))];
                if (names.Length == 0 || !sessionClient.IsConnected) continue;

                await sessionClient.SendAsync(null, new FileWantedEvent(names)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                });
            }
        }

        private bool HasOrIsReceiving(string name) =>
            MediaFileResolver.ResolveLocalOriginal(name) != null
            || File.Exists(MediaFileResolver.ResolveLocalTempPath(name))
            || fileTransferManager.IsReceivingFile(name);

        private void HandleFileSharingRequest(EditEvent editEvent)
        {
            if (!IsKeptRoom || !IsConnected) return;

            switch (editEvent)
            {
                case FileWantedEvent wanted:
                    OfferWantedFiles(wanted);
                    break;
                case FileOfferEvent offer:
                    PullOfferedFiles(offer);
                    break;
                case FilePullEvent pull:
                    _ = ServePulledFileAsync(pull);
                    break;
            }
        }

        private void OfferWantedFiles(FileWantedEvent evt)
        {
            Dictionary<string, string>? projectFiles = null;

            var available = evt.FileNames
                .Take(MaxFilesPerRequest)
                .Where(name => ResolveServablePath(name, ref projectFiles) != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (available.Length == 0) return;

            _ = sessionClient.SendAsync(evt.ExecutorId.ToString(), new FileOfferEvent(available)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
        }

        private void PullOfferedFiles(FileOfferEvent evt)
        {
            var now = DateTime.UtcNow;

            foreach (var name in evt.FileNames.Take(MaxFilesPerRequest))
            {
                lock (wantedFiles)
                {
                    if (!wantedFiles.ContainsKey(name)) continue;
                    if (pulledFiles.TryGetValue(name, out var pulledAt) && now - pulledAt < FileWantRetry) continue;

                    pulledFiles[name] = now;
                }

                if (HasOrIsReceiving(name)) continue;

                _ = sessionClient.SendAsync(evt.ExecutorId.ToString(), new FilePullEvent(name)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                });
            }
        }

        private async Task ServePulledFileAsync(FilePullEvent evt)
        {
            Dictionary<string, string>? projectFiles = null;
            if (ResolveServablePath(evt.FileName, ref projectFiles) is not { } path) return;

            if (!MediaFileResolver.IsReceivedCopy(path) && !await fileTransferManager.ConfirmSendAsync(path)) return;

            await fileTransferManager.SendDirectAsync(new SharedFile(path, evt.FileName), evt.ExecutorId, sessionClient, LocalUserId);
        }

        internal void HandleFileAvailable(FileAvailableEvent evt)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var needsTransfer = await fileTransferManager.NeedsTransferAsync(evt);

                    var requestEvt = new FileRequestEvent(evt.TransferId, LocalUserId, needsTransfer)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    };

                    await sessionClient.SendAsync(evt.ExecutorId.ToString(), requestEvt);
                }
                catch
                {
                }
            });
        }

        internal void HandleFileRequest(FileRequestEvent evt)
        {
            _ = fileTransferManager.HandleFileRequestAsync(evt, sessionClient, LocalUserId);
        }

        internal void HandleFileTransferStart(FileTransferStartEvent evt)
        {
            fileTransferManager.HandleTransferStart(evt);
        }

        internal void HandleFileChunk(FileChunkEvent evt)
        {
            fileTransferManager.HandleChunk(evt);
        }

        public void CancelIncomingTransfer(Guid transferId)
        {
            var senderId = fileTransferManager.CancelIncoming(transferId);
            if (senderId == Guid.Empty) return;

            var evt = new FileTransferCancelEvent(transferId, LocalUserId)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };

            _ = sessionClient.SendAsync(senderId.ToString(), evt);
        }

        public Task SendFileAsync(string filePath) =>
            fileTransferManager.SendFileAsync(filePath, sessionClient, LocalUserId);
    }
}
