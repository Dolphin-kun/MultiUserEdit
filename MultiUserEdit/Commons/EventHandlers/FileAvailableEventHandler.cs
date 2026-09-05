using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class FileAvailableEventHandler : IClientEventHandler<FileAvailableEvent>
    {
        public void Handle(FileAvailableEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent.ExecutorId == viewModel.LocalUserId) return;
            viewModel.HandleFileAvailable(editEvent);
        }
    }
}
