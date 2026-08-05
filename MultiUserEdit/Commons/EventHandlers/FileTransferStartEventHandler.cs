using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class FileTransferStartEventHandler : IClientEventHandler<FileTransferStartEvent>
    {
        public void Handle(FileTransferStartEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent.ExecutorId == viewModel.LocalUserId) return;
            viewModel.HandleFileTransferStart(editEvent);
        }
    }
}
