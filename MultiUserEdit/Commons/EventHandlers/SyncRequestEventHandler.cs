using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class SyncRequestEventHandler : IClientEventHandler<SyncRequestEvent>
    {
        public void Handle(SyncRequestEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleSyncRequestEvent(editEvent);
        }
    }
}
