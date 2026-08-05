using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class SceneRenamedEventHandler : IClientEventHandler<SceneRenamedEvent>
    {
        public void Handle(SceneRenamedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            viewModel.ExecuteRemoteAction(() =>
            {
                try
                {
                    timeline.Name = editEvent.NewName;
                }
                catch { }
            });
        }
    }
}
