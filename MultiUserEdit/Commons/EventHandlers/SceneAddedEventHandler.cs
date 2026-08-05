using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class SceneAddedEventHandler : IClientEventHandler<SceneAddedEvent>
    {
        public void Handle(SceneAddedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleSceneAdded(editEvent);
        }
    }
}
