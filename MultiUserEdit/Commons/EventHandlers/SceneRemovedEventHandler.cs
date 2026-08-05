using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class SceneRemovedEventHandler : IClientEventHandler<SceneRemovedEvent>
    {
        public void Handle(SceneRemovedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleSceneRemoved(editEvent);
        }
    }
}
