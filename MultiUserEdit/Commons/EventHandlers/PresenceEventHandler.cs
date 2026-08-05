using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class PresenceEventHandler : IClientEventHandler<PresenceEvent>
    {
        public void Handle(PresenceEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandlePresenceEvent(editEvent);
        }
    }
}
