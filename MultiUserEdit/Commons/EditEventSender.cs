using MultiUserEdit.Commons.Events;
using MultiUserEdit.Networking;
using Newtonsoft.Json.Linq;
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

        private readonly System.Threading.Lock cursorLock = new();
        private DateTime lastCursorSentTime = DateTime.MinValue;
        private bool cursorFlushScheduled;
        private (int Frame, int TimelineIndex, bool IsPlaying) latestCursor;

        private class MoveThrottleState
        {
            public readonly object Lock = new();
            public DateTime LastSent = DateTime.MinValue;
            public bool FlushScheduled;
            public bool Cancelled;
            public int TimelineIndex;
            public int Frame;
            public int Length;
            public int Layer;
        }

        private readonly ConcurrentDictionary<Guid, MoveThrottleState> itemMovedThrottles = new();

        public void ClearItemThrottleState(Guid itemId)
        {
            if (itemMovedThrottles.TryRemove(itemId, out var moveState))
            {
                lock (moveState.Lock) moveState.Cancelled = true;
            }

            itemUpdateThrottles.TryRemove(itemId, out _);
            lastSentItemJson.TryRemove(itemId, out _);
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

        public Task SendCursorMovedThrottledAsync(int currentFrame, int timelineIndex, bool isPlaying)
        {
            lock (cursorLock)
            {
                latestCursor = (currentFrame, timelineIndex, isPlaying);

                var now = DateTime.UtcNow;
                if ((now - lastCursorSentTime).TotalMilliseconds >= 33)
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
                await Task.Yield();

                if (!await ConfirmAllAsync(MediaFileResolver.GetTransferableFilePaths(item))) return;

                var itemId = ItemIdManager.GetOrCreateId(item);

                var (itemJson, filesToSend) = MediaFileResolver.SerializeForSync(item);
                var mediaFileNames = filesToSend.Count > 0 ? filesToSend.Where(file => file.ShouldTransfer).Select(file => file.Name).ToList() : null;
                var characterName = CharacterResolver.GetCharacter(item)?.Name;

                var evt = new ItemAddedEvent(itemId, timelineIndex, ItemTypeResolver.GetTypeName(item.GetType()), itemJson, frame, layer, mediaFileNames)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                SetBaseline(itemId, itemJson);
                await sessionClient.SendAsync(null, evt);

                foreach (var file in filesToSend.Where(file => file.ShouldTransfer))
                {
                    _ = fileTransferManager.TransferAsync(file, characterName, sessionClient, LocalUserId);
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
                if ((now - state.LastSent).TotalMilliseconds >= 33)
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
                            if (state.Cancelled) return;
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

        private async Task<bool> ConfirmAllAsync(IReadOnlyList<string> filePaths)
        {
            if (filePaths.Count == 0) return true;

            var results = await Task.WhenAll(filePaths.Select(fileTransferManager.ConfirmSendAsync));
            return results.All(allowed => allowed);
        }

        private readonly ConcurrentDictionary<Guid, string> lastSentItemJson = new();

        public void SetBaseline(Guid itemId, string itemJson) => lastSentItemJson[itemId] = itemJson;

        public void ClearBaseline(Guid itemId) => lastSentItemJson.TryRemove(itemId, out _);

        public void ClearAllBaselines() => lastSentItemJson.Clear();

        private string? BuildPayload(Guid itemId, string itemJson)
        {
            if (!lastSentItemJson.TryGetValue(itemId, out var baseline)) return itemJson;

            try
            {
                var previous = JObject.Parse(baseline);
                var current = JObject.Parse(itemJson);
                var payload = new JObject();
                var changed = false;

                foreach (var property in current.Properties())
                {
                    if (property.Name == TypeProperty)
                    {
                        payload[property.Name] = property.Value;
                        continue;
                    }

                    var before = previous[property.Name];
                    if (before != null && JToken.DeepEquals(before, property.Value)) continue;

                    payload[property.Name] = property.Value;
                    changed = true;
                }

                foreach (var property in previous.Properties())
                {
                    if (property.Name == TypeProperty) continue;
                    if (current[property.Name] != null) continue;

                    payload[property.Name] = JValue.CreateNull();
                    changed = true;
                }

                return changed ? payload.ToString(Newtonsoft.Json.Formatting.None) : null;
            }
            catch
            {
                return itemJson;
            }
        }

        private const string TypeProperty = "$type";

        public Func<IItem, bool>? IsItemAlive { get; set; }

        public async Task SendItemUpdatedAsync(IItem item, int timelineIndex)
        {
            if (!sessionClient.IsConnected) return;

            try
            {
                await Task.Yield();

                if (IsItemAlive?.Invoke(item) == false) return;

                if (!await ConfirmAllAsync(MediaFileResolver.GetTransferableFilePaths(item))) return;

                var (itemJson, files) = MediaFileResolver.SerializeForSync(item, filter: fileTransferManager.IsSendAllowed);
                var mediaFileNames = files.Count > 0 ? files.Where(file => file.ShouldTransfer).Select(file => file.Name).ToList() : null;
                var characterName = CharacterResolver.GetCharacter(item)?.Name;
                var itemId = ItemIdManager.GetOrCreateId(item);

                var payload = BuildPayload(itemId, itemJson);
                if (payload == null) return;

                lastSentItemJson[itemId] = itemJson;

                var evt = new ItemUpdatedEvent(itemId, timelineIndex, payload, mediaFileNames)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);

                foreach (var file in files)
                {
                    _ = fileTransferManager.TransferAsync(file, characterName, sessionClient, LocalUserId);
                }
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

        private const int CoalesceDelayMilliseconds = 16;
        private const int MinimumSendIntervalMilliseconds = 33;

        public Task SendItemUpdatedThrottledAsync(IItem item, int timelineIndex)
        {
            var itemId = ItemIdManager.GetOrCreateId(item);
            var state = itemUpdateThrottles.GetOrAdd(itemId, static _ => new UpdateThrottleState());

            lock (state.Lock)
            {
                state.Item = item;
                state.TimelineIndex = timelineIndex;

                if (state.FlushScheduled) return Task.CompletedTask;

                var elapsed = (DateTime.UtcNow - state.LastSent).TotalMilliseconds;
                var delay = (int)Math.Max(CoalesceDelayMilliseconds, MinimumSendIntervalMilliseconds - elapsed);

                state.FlushScheduled = true;
                _ = Task.Delay(delay).ContinueWith(_ =>
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

        public async Task SendVideoInfoUpdatedAsync(int index, int width, int height, int fps, int hz, string? backgroundColor)
        {
            try
            {
                var evt = new VideoInfoUpdatedEvent(index, width, height, fps, hz, backgroundColor)
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
