using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class CharacterRequestEventHandler : IClientEventHandler<CharacterRequestEvent>
    {
        public void Handle(CharacterRequestEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent.ExecutorId == viewModel.LocalUserId) return;
            viewModel.HandleCharacterRequest(editEvent);
        }
    }
}
