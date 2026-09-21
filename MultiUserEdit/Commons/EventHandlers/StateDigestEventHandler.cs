using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class StateDigestEventHandler : IClientEventHandler<StateDigestEvent>
    {
        public void Handle(StateDigestEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleStateDigest(editEvent);
        }
    }
}
