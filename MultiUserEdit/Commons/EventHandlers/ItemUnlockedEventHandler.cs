using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemUnlockedEventHandler : IClientEventHandler<ItemUnlockedEvent>
    {
        public void Handle(ItemUnlockedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleItemUnlockedEvent(editEvent);
        }
    }
}
