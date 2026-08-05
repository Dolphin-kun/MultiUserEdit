using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemLockedEventHandler : IClientEventHandler<ItemLockedEvent>
    {
        public void Handle(ItemLockedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleItemLockedEvent(editEvent);
        }
    }
}
