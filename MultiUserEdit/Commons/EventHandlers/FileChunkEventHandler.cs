using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class FileChunkEventHandler : IClientEventHandler<FileChunkEvent>
    {
        public void Handle(FileChunkEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent.ExecutorId == viewModel.LocalUserId) return;
            viewModel.HandleFileChunk(editEvent);
        }
    }
}
