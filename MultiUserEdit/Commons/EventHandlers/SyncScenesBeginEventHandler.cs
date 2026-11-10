using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class SyncScenesBeginEventHandler : IClientEventHandler<SyncScenesBeginEvent>
    {
        public void Handle(SyncScenesBeginEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleSyncScenesBegin(editEvent);
        }
    }
}
