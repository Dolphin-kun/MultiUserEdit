using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class SyncScenesChunkEventHandler : IClientEventHandler<SyncScenesChunkEvent>
    {
        public void Handle(SyncScenesChunkEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleSyncScenesChunk(editEvent);
        }
    }
}
