using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemMovedEventHandler : IClientEventHandler<ItemMovedEvent>
    {
        public void Handle(ItemMovedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            var item = timeline.Items.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == editEvent.ItemId);
            if (item == null) return;

            item.Frame = editEvent.Frame;
            item.Length = editEvent.Length;
            item.Layer = editEvent.Layer;

            timeline.ResolveItemCollision(item);
            timeline.RefreshTimelineLengthAndMaxLayer();
            viewModel.UpdateItemJsonCache(item);
        }
    }
}
