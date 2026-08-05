using MultiUserEdit.Commons.Events;
using MultiUserEdit.Networking;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal class EditEventSender(SessionClient sessionClient, FileTransferManager fileTransferManager, Func<Guid> getLocalUserIdFunc)
    {
        private readonly SessionClient sessionClient = sessionClient;
        private readonly FileTransferManager fileTransferManager = fileTransferManager;
        private readonly Func<Guid> getLocalUserIdFunc = getLocalUserIdFunc;

        private Guid LocalUserId => getLocalUserIdFunc();

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

        public async Task SendItemAddedAsync(IItem item, int frame, int layer, int timelineIndex)
        {
            try
            {
                var itemJson = Newtonsoft.Json.JsonConvert.SerializeObject(item, ItemSerializerOptions.Default);
                var itemId = ItemIdManager.GetOrCreateId(item);
                var evt = new ItemAddedEvent(itemId, timelineIndex, item.GetType().AssemblyQualifiedName!, itemJson, frame, layer)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);

                var filePath = MediaFileResolver.GetFilePath(item);
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    var tempDir = FileTransferManager.GetSaveDirectory();
                    if (tempDir != null && !filePath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                    {
                        _ = fileTransferManager.SendFileAsync(filePath, sessionClient, LocalUserId);
                    }
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
            try
            {
                var itemJson = Newtonsoft.Json.JsonConvert.SerializeObject(item, ItemSerializerOptions.Default);
                var itemId = ItemIdManager.GetOrCreateId(item);
                var evt = new ItemUpdatedEvent(itemId, timelineIndex, itemJson)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
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
