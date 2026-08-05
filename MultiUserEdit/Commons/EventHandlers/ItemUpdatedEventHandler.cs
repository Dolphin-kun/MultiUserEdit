using Newtonsoft.Json;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;
using System.IO;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemUpdatedEventHandler : IClientEventHandler<ItemUpdatedEvent>
    {
        public void Handle(ItemUpdatedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            var item = timeline.Items.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == editEvent.ItemId);
            if (item == null) return;

            try
            {
                JsonConvert.PopulateObject(editEvent.ItemJson, item, ItemSerializerOptions.Default);

                var currentPath = MediaFileResolver.GetFilePath(item);
                if (!string.IsNullOrEmpty(currentPath) && !File.Exists(currentPath))
                {
                    var resolvedPath = MediaFileResolver.ResolveLocalTempPath(currentPath);
                    MediaFileResolver.SetFilePath(item, resolvedPath);
                }

                CharacterResolver.TryResolveCharacter(item);
                SceneResolver.ResolveSceneId(item, viewModel.Scenes);
                viewModel.UpdateItemJsonCache(item);
            }
            catch { }
        }
    }
}
