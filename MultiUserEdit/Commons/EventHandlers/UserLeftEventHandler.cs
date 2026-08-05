using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class UserLeftEventHandler : IClientEventHandler<UserLeftEvent>
    {
        public void Handle(UserLeftEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleUserLeftEvent(editEvent);
        }
    }
}
