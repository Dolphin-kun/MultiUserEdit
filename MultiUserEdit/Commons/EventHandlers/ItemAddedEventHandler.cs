using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemAddedEventHandler : IClientEventHandler<ItemAddedEvent>
    {
        public void Handle(ItemAddedEvent editEvent, MultiUserEditViewModel viewModel) =>
            Handle(editEvent, viewModel, canWait: true);

        private static void Handle(ItemAddedEvent editEvent, MultiUserEditViewModel viewModel, bool canWait)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            if (timeline.Items.Any(existing => ItemIdManager.GetOrCreateId(existing) == editEvent.ItemId)) return;

            try
            {
                var itemType = ItemTypeResolver.Resolve(editEvent.ItemTypeName);
                if (itemType == null)
                {
                    if (editEvent.ExecutorId != viewModel.LocalUserId)
                    {
                        ResourceAvailabilityChecker.Notify(editEvent.ItemId, editEvent.ExecutorId,
                            viewModel.GetUserName(editEvent.ExecutorId), editEvent.ItemJson, editEvent.ItemTypeName);
                    }
                    return;
                }

                var isOwnEvent = editEvent.ExecutorId == viewModel.LocalUserId;
                if (isOwnEvent)
                {
                    AddItem(editEvent, viewModel, timeline, itemType, editEvent.ItemJson);
                    return;
                }

                var characterName = MediaFileResolver.GetCharacterNameFromJson(editEvent.ItemJson);
                if (canWait && !string.IsNullOrEmpty(characterName) && !viewModel.IsCharacterDecided(characterName))
                {
                    viewModel.RequestCharacter(characterName, editEvent.ExecutorId, () => Handle(editEvent, viewModel, canWait: true));
                    return;
                }

                var tachieBaseDirectory = MediaFileResolver.GetTachieBaseDirectoryFromJson(editEvent.ItemJson);

                var requiresRealContainer = MediaFileResolver.RequiresRealMediaContainer(itemType);

                var missingFiles = requiresRealContainer
                    ? MediaFileResolver.GetMissingFileNames(editEvent.MediaFileNames, tachieBaseDirectory)
                    : MediaFileResolver.GetMissingFileNames(editEvent.MediaFileNames, itemType, tachieBaseDirectory);

                if (canWait && missingFiles.Count > 0)
                {
                    TransferWaiter.WhenFilesReady(viewModel, missingFiles,
                        () => Handle(editEvent, viewModel, canWait: false), TransferWaiter.DefaultTimeout);
                    return;
                }

                if (requiresRealContainer && missingFiles.Count > 0)
                {
                    ErrorNotifier.NotifyOnce(
                        "動画・音声ファイルを受信できませんでした",
                        $"{viewModel.GetUserName(editEvent.ExecutorId)} が追加したアイテムのファイルが届かなかったため、アイテムを追加できませんでした。\n\n"
                        + string.Join("\n", missingFiles.Take(5).Select(name => "・" + name))
                        + "\n\n送信側で「データを同期する」を実行すると、もう一度送信されます。");
                    return;
                }

                var itemJson = MediaFileResolver.ResolveJsonFileReferences(editEvent.ItemJson, itemType, editEvent.MediaFileNames, tachieBaseDirectory);

                ResourceAvailabilityChecker.Notify(editEvent.ItemId, editEvent.ExecutorId,
                    viewModel.GetUserName(editEvent.ExecutorId), itemJson, null);

                var item = AddItem(editEvent, viewModel, timeline, itemType, itemJson);
                if (item == null) return;

                var pendingFiles = MediaFileResolver.GetMissingFileNames(editEvent.MediaFileNames, tachieBaseDirectory);
                if (canWait && pendingFiles.Count > 0)
                {
                    TransferWaiter.WhenFilesReady(viewModel, pendingFiles, () =>
                        Newtonsoft.Json.JsonConvert.PopulateObject(
                            MediaFileResolver.ResolveJsonFileReferences(editEvent.ItemJson, itemType, editEvent.MediaFileNames, tachieBaseDirectory),
                            item,
                            ItemSerializerOptions.Default));
                }
            }
            catch (Exception ex)
            {
                ErrorNotifier.NotifyOnce(
                    "アイテムを追加できませんでした",
                    $"種類: {editEvent.ItemTypeName}\n{ex.Message}");
            }
        }

        private static IItem? AddItem(ItemAddedEvent editEvent, MultiUserEditViewModel viewModel, Timeline timeline, Type itemType, string itemJson)
        {
            if (Newtonsoft.Json.JsonConvert.DeserializeObject(itemJson, itemType, ItemSerializerOptions.Default) is not IItem item)
                return null;

            CharacterResolver.TryResolveCharacter(item);
            SceneResolver.ResolveSceneId(item, viewModel.Scenes);

            ItemIdManager.RegisterId(item, editEvent.ItemId);

            if (!timeline.TryAddItems([item], editEvent.Frame, editEvent.Layer, false))
            {
                ErrorNotifier.NotifyOnce(
                    "アイテムを追加できませんでした",
                    $"レイヤー {editEvent.Layer + 1} 付近に空きが無いため、共同編集相手が追加したアイテムを配置できませんでした。");
                return null;
            }

            return item;
        }
    }
}
