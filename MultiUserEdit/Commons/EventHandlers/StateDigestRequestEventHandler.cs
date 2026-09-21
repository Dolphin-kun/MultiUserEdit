using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class StateDigestRequestEventHandler : IClientEventHandler<StateDigestRequestEvent>
    {
        public void Handle(StateDigestRequestEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleStateDigestRequest(editEvent);
        }
    }
}
