using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class FileTransferCancelEventHandler : IClientEventHandler<FileTransferCancelEvent>
    {
        public void Handle(FileTransferCancelEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent.ExecutorId == viewModel.LocalUserId) return;
            viewModel.HandleFileTransferCancel(editEvent);
        }
    }
}
