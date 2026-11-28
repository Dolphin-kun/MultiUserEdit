using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using Newtonsoft.Json;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        private bool IsFromHost(Guid userId)
        {
            if (userId == Guid.Empty) return false;

            var hostId = VerifiedHostId;
            if (hostId != Guid.Empty) return userId == hostId;

            return Participants.Any(p => p.UserId == userId && p.Role == UserRole.Host);
        }

        private Guid CurrentHostId
        {
            get
            {
                var hostId = VerifiedHostId;
                if (hostId != Guid.Empty) return hostId;

                return Participants.FirstOrDefault(p => p.Role == UserRole.Host)?.UserId ?? Guid.Empty;
            }
        }

        private UserRole NormalizeRole(Guid userId, UserRole claimed)
        {
            var hostId = VerifiedHostId;
            if (hostId == Guid.Empty) return claimed;

            return userId == hostId ? UserRole.Host : UserRole.Guest;
        }

        private UserPermission PermissionOf(Guid userId)
        {
            if (IsFromHost(userId)) return FullPermission;

            return permissionOverrides.TryGetValue(userId, out var permission) ? permission : GlobalDefaultPermission;
        }

        private bool IsAllowedFrom(EditEvent editEvent)
        {
            if (!SyncScenesTransfer.IsProjectEdit(editEvent)) return true;
            if (TouchesVoiceFromWeb(editEvent)) return false;

            var permission = PermissionOf(editEvent.ExecutorId);

            return editEvent switch
            {
                ItemAddedEvent => permission.CanAddItems,
                ItemRemovedEvent => permission.CanDeleteItems,
                ItemMovedEvent => permission.CanMoveItems,
                ItemUpdatedEvent => permission.CanEditProperties,
                SceneAddedEvent => permission.CanAddScenes,
                SceneRemovedEvent => permission.CanDeleteScenes,
                _ => true
            };
        }

        private static readonly string[] VoiceSourceProperties =
            ["CharacterName", "Serif", "Hatsuon", "VoiceParameter", "Pronounce", "VoiceCache", "VoiceLength", "LipSyncFrames"];

        private bool TouchesVoiceFromWeb(EditEvent editEvent)
        {
            if (!webParticipants.ContainsKey(editEvent.ExecutorId)) return false;

            return editEvent switch
            {
                ItemAddedEvent added => ItemTypeResolver.Resolve(added.ItemTypeName) is { } type && typeof(VoiceItem).IsAssignableFrom(type),
                ItemRemovedEvent removed => FindItem(removed.TimelineIndex, removed.ItemId) is VoiceItem,
                ItemUpdatedEvent updated => FindItem(updated.TimelineIndex, updated.ItemId) is VoiceItem && NamesVoiceSource(updated.ItemJson),
                _ => false
            };
        }

        private IItem? FindItem(int timelineIndex, Guid itemId) =>
            Scenes?.Timelines.ElementAtOrDefault(timelineIndex)?.Items.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == itemId);

        private static bool NamesVoiceSource(string itemJson)
        {
            try
            {
                return Newtonsoft.Json.Linq.JObject.Parse(itemJson).Properties().Any(p => VoiceSourceProperties.Contains(p.Name));
            }
            catch (JsonException)
            {
                return true;
            }
        }

        private string? DescribeReceiveBlock(Guid senderId, string fileName)
        {
            var hostId = VerifiedHostId;
            if (hostId == Guid.Empty || senderId == hostId) return null;

            var permission = permissionOverrides.TryGetValue(senderId, out var granted) ? granted : GlobalDefaultPermission;
            if (!permission.CanShareFiles) return "file sharing is not allowed for this sender";

            var allowed = FileExtensionCatalog.GetKindForFile(fileName) switch
            {
                FileGroupKind.Video => permission.CanShareVideos,
                FileGroupKind.Image => permission.CanShareImages,
                FileGroupKind.Audio => permission.CanShareAudio,
                _ => true
            };

            return allowed ? null : "this file type is not allowed for this sender";
        }

        private bool IsLocalChangeAllowed(bool isMove) =>
            !IsConnected || IsHost || (isMove ? CurrentUserPermission.CanMoveItems : CurrentUserPermission.CanEditProperties);

        private void RejectLocalChange(Guid itemId)
        {
            lock (rejectedLocalChanges) rejectedLocalChanges.Add(itemId);
        }

        private void UndoRedoManager_Recorded(object? sender, EventArgs e) => RestoreRejectedChanges();

        private void RestoreRejectedChanges()
        {
            Guid[] itemIds;
            lock (rejectedLocalChanges)
            {
                if (rejectedLocalChanges.Count == 0) return;

                itemIds = [.. rejectedLocalChanges];
                rejectedLocalChanges.Clear();
            }

            var hostId = CurrentHostId;
            if (hostId == Guid.Empty || hostId == LocalUserId) return;

            foreach (var itemId in itemIds) RequestItemState(itemId, hostId);

            ErrorNotifier.NotifyOnce(
                Texts.CannotChangeItemTitle,
                Texts.CannotChangeItemMessage);
        }

        private void TrackRemoteSceneChanges()
        {
            if (Scenes == null) return;

            var current = Scenes.Timelines.ToList();

            if (activeViewModel != null)
            {
                foreach (var timeline in current)
                {
                    if (!scenesSnapshot.Contains(timeline)) timelineSyncManager.RegisterTimeline(timeline, activeViewModel);
                }
            }

            foreach (var timeline in scenesSnapshot)
            {
                if (!current.Contains(timeline)) timelineSyncManager.UnregisterTimeline(timeline);
            }

            scenesSnapshot = current;
        }

        private readonly Dictionary<Guid, DateTime> lockedItemsReceivedAt = [];

        public event Action<string, string>? FileTransferCompleted;
        public event Action? SentFilesChanged;

        private void OnUndoRedoCommandCreated(object? sender, EventArgs e)
        {
            if (isApplyingRemoteEvent || !IsConnected) return;
            if (sender is IItem or Timeline or YukkuriMovieMaker.Project.Scenes or VideoInfo) return;

            if (FirstOrDefaultTimeline != null && Scenes != null)
            {
                var index = Scenes.Timelines.IndexOf(FirstOrDefaultTimeline);
                if (index < 0) index = 0;

                foreach (var item in FirstOrDefaultTimeline.SelectedItems)
                {
                    _ = eventSender.SendItemUpdatedThrottledAsync(item, index);
                }
            }
        }

        public void LockItemLocally(Guid itemId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            locallyLockedItems[itemId] = timestamp;
            _ = eventSender.SendItemLockAsync(itemId, timestamp);
        }

        public void UnlockItemLocally(Guid itemId)
        {
            if (!locallyLockedItems.Remove(itemId)) return;
            _ = eventSender.SendItemUnlockAsync(itemId);
        }

        internal bool IsReceivingFiles() => fileTransferManager.IsReceivingAny();

        private readonly HashSet<Guid> locallyAddedItems = [];

        private void NoteLocallyAddedItem(IItem item)
        {
            try
            {
                lock (locallyAddedItems) locallyAddedItems.Add(ItemIdManager.GetOrCreateId(item));
            }
            catch
            {
            }
        }

        private bool WasAddedLocally(IItem item)
        {
            var itemId = ItemIdManager.GetOrCreateId(item);

            lock (locallyAddedItems) return locallyAddedItems.Contains(itemId);
        }

        private string? DescribeFileSendBlock(string filePath)
        {
            if (IsHost) return null;

            var permission = CurrentUserPermission;
            if (!permission.CanShareFiles)
                return Texts.SendBlockedAll;

            return FileExtensionCatalog.GetKindForFile(filePath) switch
            {
                FileGroupKind.Video when !permission.CanShareVideos => Texts.SendBlockedVideo,
                FileGroupKind.Image when !permission.CanShareImages => Texts.SendBlockedImage,
                FileGroupKind.Audio when !permission.CanShareAudio => Texts.SendBlockedAudio,
                _ => null
            };
        }

        private bool RejectRestrictedRemoval(IReadOnlyList<IItem> removed, Timeline timeline)
        {
            if (!IsConnected || IsHost) return false;

            var othersOnly = CurrentUserPermission.CanDeleteItems
                && !CurrentUserPermission.CanDeleteOthersItems
                && removed.Any(item => !WasAddedLocally(item));

            if (CurrentUserPermission.CanDeleteItems && !othersOnly) return false;

            var previous = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            try
            {
                using var undoScope = UndoRecordSuppressor.Suppress(undoRedoManager);
                foreach (var item in removed) timeline.TryAddItems([item], item.Frame, item.Layer, false);
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not restore an item whose removal was not allowed", ex);
            }
            finally
            {
                isApplyingRemoteEvent = previous;
            }

            if (othersOnly)
            {
                ErrorNotifier.NotifyOnce(
                    Texts.CannotDeleteItemTitle,
                    Texts.DeleteOwnOnly);
            }
            else
            {
                ErrorNotifier.NotifyOnce(
                    Texts.CannotDeleteItemTitle,
                    Texts.DeleteNotAllowed);
            }

            return true;
        }

        private bool RejectRestrictedAddition(IReadOnlyList<IItem> added, Timeline timeline)
        {
            if (!IsConnected || IsHost || CurrentUserPermission.CanAddItems) return false;

            var previous = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            try
            {
                using var undoScope = UndoRecordSuppressor.Suppress(undoRedoManager);
                timeline.DeleteItems([.. added]);
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not undo an item addition that was not allowed", ex);
            }
            finally
            {
                isApplyingRemoteEvent = previous;
            }

            ErrorNotifier.NotifyOnce(
                Texts.CannotAddItemTitle,
                Texts.AddNotAllowed);

            return true;
        }

        internal bool IsItemEditableLocally(Guid itemId) =>
            locallyLockedItems.ContainsKey(itemId) || !LockedItems.ContainsKey(itemId);

        internal void HandleItemStateRequest(ItemStateRequestEvent evt)
        {
            if (!IsConnected || Scenes == null) return;

            foreach (var timeline in Scenes.Timelines)
            {
                var item = timeline.Items.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == evt.ItemId);
                if (item == null) continue;

                eventSender.ClearBaseline(evt.ItemId);
                _ = eventSender.SendItemUpdatedAsync(item, Scenes.Timelines.IndexOf(timeline));
                return;
            }
        }

        private void RequestItemState(Guid itemId, Guid ownerId)
        {
            if (!IsConnected) return;

            var request = new ItemStateRequestEvent(itemId, LocalUserId)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            _ = sessionClient.SendAsync(ownerId.ToString(), request);
        }
    }
}
