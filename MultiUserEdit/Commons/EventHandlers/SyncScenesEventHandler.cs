using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class SyncScenesEventHandler : IClientEventHandler<SyncScenesEvent>
    {
        public void Handle(SyncScenesEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.ApplySyncScenes(editEvent.Scenes);
        }
    }
}
