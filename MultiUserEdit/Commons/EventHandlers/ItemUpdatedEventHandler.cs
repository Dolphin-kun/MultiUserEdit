using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;
using Newtonsoft.Json;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemUpdatedEventHandler : IClientEventHandler<ItemUpdatedEvent>
    {
        public void Handle(ItemUpdatedEvent editEvent, MultiUserEditViewModel viewModel) =>
            Handle(editEvent, viewModel, canWait: true);

        private static void Handle(ItemUpdatedEvent editEvent, MultiUserEditViewModel viewModel, bool canWait)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            var item = timeline.Items.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == editEvent.ItemId);
            if (item == null) return;

            try
            {
                var itemJson = MediaFileResolver.ResolveJsonFileReferences(editEvent.ItemJson, item, editEvent.MediaFileNames);
                FontAvailabilityChecker.NotifyMissingFonts(itemJson, viewModel.GetUserName(editEvent.ExecutorId));
                JsonConvert.PopulateObject(itemJson, item, ItemSerializerOptions.Default);
                viewModel.EventSender?.ClearBaseline(editEvent.ItemId);

                CharacterResolver.TryResolveCharacter(item);
                SceneResolver.ResolveSceneId(item, viewModel.Scenes);

                var missingFiles = MediaFileResolver.GetMissingFileNames(
                    editEvent.MediaFileNames, TachieFileResolver.GetBaseDirectory(item));
                if (canWait && missingFiles.Count > 0)
                {
                    TransferWaiter.WhenFilesReady(viewModel, missingFiles,
                        () => Handle(editEvent, viewModel, canWait: false), TransferWaiter.DefaultTimeout);
                }
            }
            catch { }
        }
    }
}
