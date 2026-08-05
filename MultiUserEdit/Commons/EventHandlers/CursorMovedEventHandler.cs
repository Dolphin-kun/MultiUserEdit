using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class CursorMovedEventHandler : IClientEventHandler<CursorMovedEvent>
    {
        public void Handle(CursorMovedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleCursorMovedEvent(editEvent);
        }
    }
}