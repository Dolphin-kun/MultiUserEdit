using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class FileRequestEventHandler : IClientEventHandler<FileRequestEvent>
    {
        public void Handle(FileRequestEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent.ExecutorId == viewModel.LocalUserId) return;
            viewModel.HandleFileRequest(editEvent);
        }
    }
}
