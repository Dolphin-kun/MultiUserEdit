using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemRemovedEventHandler : IClientEventHandler<ItemRemovedEvent>
    {
        public void Handle(ItemRemovedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            var targetItem = timeline.Items.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == editEvent.ItemId);
            if (targetItem == null) return;

            timeline.DeleteItems([targetItem]);
        }
    }
}
