using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class VideoInfoUpdatedEventHandler : IClientEventHandler<VideoInfoUpdatedEvent>
    {
        public void Handle(VideoInfoUpdatedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            viewModel.ExecuteRemoteAction(() =>
            {
                try
                {
                    var videoInfo = timeline.VideoInfo;
                    if (videoInfo != null)
                    {
                        videoInfo.Width = editEvent.Width;
                        videoInfo.Height = editEvent.Height;
                        videoInfo.FPS = editEvent.FPS;
                        videoInfo.Hz = editEvent.Hz;
                        if (VideoInfoSerializer.ToColor(editEvent.BackgroundColor) is { } background)
                            videoInfo.BackgroundColor = background;
                    }
                }
                catch { }
            });
        }
    }
}
