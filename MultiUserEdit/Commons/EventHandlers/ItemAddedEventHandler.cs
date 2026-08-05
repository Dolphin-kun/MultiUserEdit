using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;
using System.IO;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemAddedEventHandler : IClientEventHandler<ItemAddedEvent>
    {
        public void Handle(ItemAddedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            try
            {
                var itemType = Type.GetType(editEvent.ItemTypeName);
                if (itemType == null) return;

                if (Newtonsoft.Json.JsonConvert.DeserializeObject(editEvent.ItemJson, itemType, ItemSerializerOptions.Default) is not IItem item)
                    return;

                CharacterResolver.TryResolveCharacter(item);
                SceneResolver.ResolveSceneId(item, viewModel.Scenes);

                if (editEvent.ExecutorId == viewModel.LocalUserId)
                {
                    ItemIdManager.RegisterId(item, editEvent.ItemId);
                    timeline.TryAddItems([item], editEvent.Frame, editEvent.Layer, false);
                    return;
                }

                var originalFilePath = MediaFileResolver.GetFilePath(item);

                if (!string.IsNullOrEmpty(originalFilePath))
                {
                    var targetSavePath = MediaFileResolver.ResolveLocalTempPath(originalFilePath);

                    if (!File.Exists(targetSavePath))
                    {
                        var fileName = Path.GetFileName(targetSavePath);

                        void onCompleted(string transferId, string savedPath)
                        {
                            if (Path.GetFileName(savedPath).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                            {
                                viewModel.FileTransferCompleted -= onCompleted;

                                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                                {
                                    viewModel.ExecuteRemoteAction(() =>
                                    {
                                        MediaFileResolver.SetFilePath(item, savedPath);
                                        ItemIdManager.RegisterId(item, editEvent.ItemId);
                                        timeline.TryAddItems([item], editEvent.Frame, editEvent.Layer, false);
                                    });
                                });
                            }
                        }

                        viewModel.FileTransferCompleted += onCompleted;
                        return;
                    }

                    MediaFileResolver.SetFilePath(item, targetSavePath);
                }

                ItemIdManager.RegisterId(item, editEvent.ItemId);
                timeline.TryAddItems([item], editEvent.Frame, editEvent.Layer, false);
            }
            catch { }
        }
    }
}
