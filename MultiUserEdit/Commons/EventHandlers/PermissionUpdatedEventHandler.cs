using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class PermissionUpdatedEventHandler : IClientEventHandler<PermissionUpdatedEvent>
    {
        public void Handle(PermissionUpdatedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandlePermissionUpdated(editEvent);
        }
    }
}
