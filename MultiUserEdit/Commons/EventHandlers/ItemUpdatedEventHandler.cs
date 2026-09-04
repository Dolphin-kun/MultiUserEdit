using Newtonsoft.Json;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

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
                // 生きた（タイムラインに表示中の）アイテムへ直接PopulateObjectする前に、JSON文字列の段階で
                // ファイル名をローカルの解決済み絶対パスへ置き換えておく。PopulateObject後に直す方式だと、
                // その一瞬だけ壊れたファイル名だけの値がUIへ反映されてしまうため。
                var itemJson = MediaFileResolver.ResolveJsonFileReferences(editEvent.ItemJson, item, editEvent.MediaFileNames);
                JsonConvert.PopulateObject(itemJson, item, ItemSerializerOptions.Default);

                CharacterResolver.TryResolveCharacter(item);
                SceneResolver.ResolveSceneId(item, viewModel.Scenes);
            }
            catch { }
        }
    }
}
