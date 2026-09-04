using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class UserKickedEventHandler : IClientEventHandler<UserKickedEvent>
    {
        public void Handle(UserKickedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleUserKickedEvent(editEvent);
        }
    }
}
