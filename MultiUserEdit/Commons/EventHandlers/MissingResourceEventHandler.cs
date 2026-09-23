using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class MissingResourceEventHandler : IClientEventHandler<MissingResourceEvent>
    {
        public void Handle(MissingResourceEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleMissingResource(editEvent);
        }
    }
}
