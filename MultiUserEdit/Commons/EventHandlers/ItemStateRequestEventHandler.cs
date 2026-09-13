using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemStateRequestEventHandler : IClientEventHandler<ItemStateRequestEvent>
    {
        public void Handle(ItemStateRequestEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleItemStateRequest(editEvent);
        }
    }
}
