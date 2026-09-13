using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class CharacterSharedEventHandler : IClientEventHandler<CharacterSharedEvent>
    {
        public void Handle(CharacterSharedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent.ExecutorId == viewModel.LocalUserId) return;
            viewModel.HandleCharacterShared(editEvent);
        }
    }
}
