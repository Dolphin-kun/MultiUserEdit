using MultiUserEdit.Commons.Events;
using MultiUserEdit.Networking;
using System.Collections.Concurrent;
using System.IO;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal class EditEventSender(SessionClient sessionClient, FileTransferManager fileTransferManager, Func<Guid> getLocalUserIdFunc)
    {
        private readonly SessionClient sessionClient = sessionClient;
        private readonly FileTransferManager fileTransferManager = fileTransferManager;
        private readonly Func<Guid> getLocalUserIdFunc = getLocalUserIdFunc;

        private Guid LocalUserId => getLocalUserIdFunc();

        private readonly object cursorLock = new();
        private DateTime lastCursorSentTime = DateTime.MinValue;
        private bool cursorFlushScheduled;
        private (int Frame, int TimelineIndex, bool IsPlaying) latestCursor;

        private class MoveThrottleState
        {
            public readonly object Lock = new();
            public DateTime LastSent = DateTime.MinValue;
            public bool FlushScheduled;
            public int TimelineIndex;
            public int Frame;
            public int Length;
            public int Layer;
        }

        private readonly ConcurrentDictionary<Guid, MoveThrottleState> itemMovedThrottles = new();

        // アイテム削除時にitemMovedThrottles/itemUpdateThrottlesへ残ったスロットル状態を破棄する
        // （呼ばないと、セッションを使い続けるほど削除済みアイテムのエントリが際限なく蓄積する）
        public void ClearItemThrottleState(Guid itemId)
        {
            itemMovedThrottles.TryRemove(itemId, out _);
            itemUpdateThrottles.TryRemove(itemId, out _);
        }

        public async Task SendCursorMovedAsync(int currentFrame, int timelineIndex, bool isPlaying)
        {
            try
            {
                var evt = new CursorMovedEvent(currentFrame, timelineIndex, isPlaying)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        // CancellationTokenSource を使ったキャンセル方式は、遅延タスク側の Dispose と
        // 新規呼び出し側の Cancel が競合すると ObjectDisposedException を起こすため、
        // 「最新値を保持し、未スケジュールなら1回だけ遅延送信を積む」方式に変更している。
        public Task SendCursorMovedThrottledAsync(int currentFrame, int timelineIndex, bool isPlaying)
        {
            lock (cursorLock)
            {
                latestCursor = (currentFrame, timelineIndex, isPlaying);

                var now = DateTime.UtcNow;
                if ((now - lastCursorSentTime).TotalMilliseconds >= 33) // ~30 FPS max
                {
                    lastCursorSentTime = now;
                    return SendCursorMovedAsync(currentFrame, timelineIndex, isPlaying);
                }

                if (!cursorFlushScheduled)
                {
                    cursorFlushScheduled = true;
                    _ = Task.Delay(35).ContinueWith(_ =>
                    {
                        (int Frame, int TimelineIndex, bool IsPlaying) toSend;
                        lock (cursorLock)
                        {
                            cursorFlushScheduled = false;
                            lastCursorSentTime = DateTime.UtcNow;
                            toSend = latestCursor;
                        }
                        _ = SendCursorMovedAsync(toSend.Frame, toSend.TimelineIndex, toSend.IsPlaying);
                    }, TaskScheduler.Default);
                }

                return Task.CompletedTask;
            }
        }

        public async Task SendItemAddedAsync(IItem item, int frame, int layer, int timelineIndex)
        {
            if (!sessionClient.IsConnected) return;

            try
            {
                var itemId = ItemIdManager.GetOrCreateId(item);
                var tempDir = FileTransferManager.GetSaveDirectory();

                // 送信がブロックされる・ユーザーが拒否するファイルは対象から除外する。
                // 付けたまま送るとイベントだけが届き、受信側はプレースホルダーのまま永久に転送完了を待ち続ける。
                var (itemJson, filesToSend) = MediaFileResolver.SerializeForSync(item, filter: fp =>
                    !fp.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase) && fileTransferManager.ConfirmSend(fp));

                var mediaFileNames = filesToSend.Count > 0 ? filesToSend.Select(fp => Path.GetFileName(fp)!).ToList() : null;

                var evt = new ItemAddedEvent(itemId, timelineIndex, item.GetType().AssemblyQualifiedName!, itemJson, frame, layer, mediaFileNames)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);

                foreach (var fp in filesToSend)
                {
                    _ = fileTransferManager.TransferAsync(fp, sessionClient, LocalUserId);
                }
            }
            catch { }
        }

        public async Task SendItemMovedAsync(Guid itemId, int timelineIndex, int frame, int length, int layer)
        {
            try
            {
                var evt = new ItemMovedEvent(itemId, timelineIndex, frame, length, layer)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        public Task SendItemMovedThrottledAsync(Guid itemId, int timelineIndex, int frame, int length, int layer)
        {
            var state = itemMovedThrottles.GetOrAdd(itemId, static _ => new MoveThrottleState());

            lock (state.Lock)
            {
                state.TimelineIndex = timelineIndex;
                state.Frame = frame;
                state.Length = length;
                state.Layer = layer;

                var now = DateTime.UtcNow;
                if ((now - state.LastSent).TotalMilliseconds >= 33) // ~30 FPS max
                {
                    state.LastSent = now;
                    return SendItemMovedAsync(itemId, timelineIndex, frame, length, layer);
                }

                if (!state.FlushScheduled)
                {
                    state.FlushScheduled = true;
                    _ = Task.Delay(35).ContinueWith(_ =>
                    {
                        int f, l, ly, ti;
                        lock (state.Lock)
                        {
                            state.FlushScheduled = false;
                            state.LastSent = DateTime.UtcNow;
                            ti = state.TimelineIndex;
                            f = state.Frame;
                            l = state.Length;
                            ly = state.Layer;
                        }
                        _ = SendItemMovedAsync(itemId, ti, f, l, ly);
                    }, TaskScheduler.Default);
                }

                return Task.CompletedTask;
            }
        }

        public async Task SendItemRemovedAsync(Guid itemId, int timelineIndex)
        {
            try
            {
                var evt = new ItemRemovedEvent(itemId, timelineIndex)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        public async Task SendItemUpdatedAsync(IItem item, int timelineIndex)
        {
            if (!sessionClient.IsConnected) return;

            try
            {
                // 未転送のファイル参照（立ち絵の表情差分等）でローカル環境のフルパスを相手に漏らさないよう、
                // ItemAdded同様にファイル名のみへ一時的に差し替えてシリアライズする。
                // どのファイル名に差し替えたかをMediaFileNamesとして一緒に送ることで、受信側は自分の
                // アイテムの現在の状態から逆算せずに（動画・音声が初回転送中でFilePathがnullの場合でも）
                // 確実に対象を解決できる。
                var (itemJson, files) = MediaFileResolver.SerializeForSync(item);
                var mediaFileNames = files.Count > 0 ? files.Select(fp => Path.GetFileName(fp)!).ToList() : null;
                var itemId = ItemIdManager.GetOrCreateId(item);
                var evt = new ItemUpdatedEvent(itemId, timelineIndex, itemJson, mediaFileNames)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        private class UpdateThrottleState
        {
            public readonly object Lock = new();
            public DateTime LastSent = DateTime.MinValue;
            public bool FlushScheduled;
            public IItem? Item;
            public int TimelineIndex;
        }

        private readonly ConcurrentDictionary<Guid, UpdateThrottleState> itemUpdateThrottles = new();

        // 回転・拡大縮小など、タイムライン上のドラッグ操作を伴わないプロパティ変更はFrame/Layer/Lengthの
        // 変更(SendItemMovedThrottledAsync)と異なりスロットリングされておらず、キャンバス上でハンドルを
        // 連続してドラッグすると1秒間に何十回もSendItemUpdatedAsync（フルシリアライズを伴う）が発火し、
        // UIスレッドを圧迫していた。ItemMoved同様のスロットリングパターンを適用する。
        // （以前はファイル参照の書き換え周りの競合を抑える目的もあって100msにしていたが、その競合自体は
        // 根本原因を修正済みのため、SendItemMovedThrottledAsyncと同じ~30fps相当まで詰めてリアルタイム性を戻す）
        public Task SendItemUpdatedThrottledAsync(IItem item, int timelineIndex)
        {
            var itemId = ItemIdManager.GetOrCreateId(item);
            var state = itemUpdateThrottles.GetOrAdd(itemId, static _ => new UpdateThrottleState());

            lock (state.Lock)
            {
                state.Item = item;
                state.TimelineIndex = timelineIndex;

                var now = DateTime.UtcNow;
                if ((now - state.LastSent).TotalMilliseconds >= 33)
                {
                    state.LastSent = now;
                    return SendItemUpdatedAsync(item, timelineIndex);
                }

                if (!state.FlushScheduled)
                {
                    state.FlushScheduled = true;
                    _ = Task.Delay(35).ContinueWith(_ =>
                    {
                        IItem toSendItem;
                        int toSendTimelineIndex;
                        lock (state.Lock)
                        {
                            state.FlushScheduled = false;
                            state.LastSent = DateTime.UtcNow;
                            toSendItem = state.Item!;
                            toSendTimelineIndex = state.TimelineIndex;
                        }
                        _ = SendItemUpdatedAsync(toSendItem, toSendTimelineIndex);
                    }, TaskScheduler.Default);
                }

                return Task.CompletedTask;
            }
        }

        public async Task SendSceneAddedAsync(int index, string name = "")
        {
            try
            {
                var evt = new SceneAddedEvent(index, name)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        public async Task SendSceneRemovedAsync(int index)
        {
            try
            {
                var evt = new SceneRemovedEvent(index)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        public async Task SendSceneRenamedAsync(int index, string newName)
        {
            try
            {
                var evt = new SceneRenamedEvent(index, newName)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        public async Task SendVideoInfoUpdatedAsync(int index, int width, int height, int fps, int hz)
        {
            try
            {
                var evt = new VideoInfoUpdatedEvent(index, width, height, fps, hz)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        public async Task SendItemLockAsync(Guid itemId, long timestamp)
        {
            try
            {
                var evt = new ItemLockedEvent(itemId, LocalUserId, timestamp)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        public async Task SendItemUnlockAsync(Guid itemId)
        {
            try
            {
                var evt = new ItemUnlockedEvent(itemId, LocalUserId)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }
    }
}
