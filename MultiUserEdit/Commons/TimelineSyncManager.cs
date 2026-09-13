using MultiUserEdit.Commons.Models;
using MultiUserEdit.ViewModels;
using System.ComponentModel;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal class TimelineSyncManager(EditEventSender eventSender, Func<bool> getIsApplyingRemoteEventFunc, Func<Guid, bool> isItemEditableFunc)
    {
        private readonly EditEventSender eventSender = eventSender;
        private readonly Func<bool> getIsApplyingRemoteEventFunc = getIsApplyingRemoteEventFunc;
        private readonly Func<Guid, bool> isItemEditableFunc = isItemEditableFunc;

        private readonly Dictionary<IItem, ItemSubscription> itemSubscription = [];
        private readonly Dictionary<Timeline, HashSet<IItem>> timelineItemSnapshot = [];
        private readonly Dictionary<Timeline, HashSet<IItem>> timelineSelectedItemsSnapshot = [];
        private readonly Dictionary<Timeline, TimelineSubscription> timelineSubscriptions = [];

        public void RegisterTimeline(Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (timeline == null) return;

            if (timelineSubscriptions.TryGetValue(timeline, out var existingSub))
            {
                timeline.PropertyChanged -= existingSub.TimelineHandler;
                if (existingSub.VideoInfoHandler != null && timeline.VideoInfo != null)
                {
                    timeline.VideoInfo.PropertyChanged -= existingSub.VideoInfoHandler;
                }
            }

            void timelineHandler(object? s, PropertyChangedEventArgs e) => OnTimelinePropertyChanged(timeline, e, viewModel);
            timeline.PropertyChanged += timelineHandler;

            PropertyChangedEventHandler? videoInfoHandler = null;
            if (timeline.VideoInfo != null)
            {
                videoInfoHandler = (s, e) => OnVideoInfoPropertyChanged(timeline, viewModel);
                timeline.VideoInfo.PropertyChanged += videoInfoHandler;
            }

            timelineSubscriptions[timeline] = new TimelineSubscription(timelineHandler, videoInfoHandler);

            if (!timelineItemSnapshot.ContainsKey(timeline))
            {
                timelineItemSnapshot[timeline] = [.. timeline.Items];
            }

            if (!timelineSelectedItemsSnapshot.ContainsKey(timeline))
            {
                timelineSelectedItemsSnapshot[timeline] = [.. timeline.SelectedItems];
            }

            foreach (var item in timeline.Items)
            {
                SubscribeItem(item, timeline, viewModel);
            }
        }

        public void UnregisterTimeline(Timeline timeline)
        {
            if (timeline == null) return;

            if (timelineSubscriptions.TryGetValue(timeline, out var sub))
            {
                timeline.PropertyChanged -= sub.TimelineHandler;
                if (sub.VideoInfoHandler != null && timeline.VideoInfo != null)
                {
                    timeline.VideoInfo.PropertyChanged -= sub.VideoInfoHandler;
                }
                timelineSubscriptions.Remove(timeline);
            }

            foreach (var item in timeline.Items)
            {
                UnsubscribeItem(item);
            }

            timelineItemSnapshot.Remove(timeline);
            timelineSelectedItemsSnapshot.Remove(timeline);
        }

        private void OnTimelinePropertyChanged(Timeline timeline, PropertyChangedEventArgs e, MultiUserEditViewModel viewModel)
        {
            var isApplyingRemoteEvent = getIsApplyingRemoteEventFunc();
            var timelineIndex = viewModel.Scenes?.Timelines.IndexOf(timeline) ?? 0;

            if (e.PropertyName == nameof(Timeline.Items))
            {
                OnItemsCollectionChanged(timeline, timelineIndex, viewModel, suppressEvents: isApplyingRemoteEvent);
                return;
            }

            if (isApplyingRemoteEvent) return;

            if (e.PropertyName == nameof(Timeline.SelectedItems))
            {
                OnSelectedItemsCollectionChanged(timeline, viewModel);
            }
            else if (e.PropertyName == nameof(Timeline.Name))
            {
                _ = eventSender.SendSceneRenamedAsync(timelineIndex, timeline.Name);
            }
        }

        private void OnVideoInfoPropertyChanged(Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (getIsApplyingRemoteEventFunc()) return;

            var timelineIndex = viewModel.Scenes?.Timelines.IndexOf(timeline) ?? 0;
            var videoInfo = timeline.VideoInfo;
            if (videoInfo != null)
            {
                _ = eventSender.SendVideoInfoUpdatedAsync(timelineIndex, videoInfo.Width, videoInfo.Height, videoInfo.FPS, videoInfo.Hz, VideoInfoSerializer.ToText(videoInfo.BackgroundColor));
            }
        }

        private void OnItemsCollectionChanged(Timeline timeline, int timelineIndex, MultiUserEditViewModel viewModel, bool suppressEvents)
        {
            if (!timelineItemSnapshot.TryGetValue(timeline, out var oldItems))
            {
                oldItems = [];
            }

            var currentItems = new HashSet<IItem>(timeline.Items);
            var added = currentItems.Except(oldItems).ToList();
            var removed = oldItems.Except(currentItems).ToList();

            timelineItemSnapshot[timeline] = currentItems;

            foreach (var item in added)
            {
                SubscribeItem(item, timeline, viewModel);
                if (!suppressEvents)
                    _ = eventSender.SendItemAddedAsync(item, item.Frame, item.Layer, timelineIndex);
            }

            foreach (var item in removed)
            {
                UnsubscribeItem(item);
                var itemId = ItemIdManager.GetOrCreateId(item);
                eventSender.ClearItemThrottleState(itemId);
                if (!suppressEvents)
                    _ = eventSender.SendItemRemovedAsync(itemId, timelineIndex);
            }
        }

        private void OnSelectedItemsCollectionChanged(Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (!timelineSelectedItemsSnapshot.TryGetValue(timeline, out var oldSelected))
            {
                oldSelected = [];
            }

            var currentSelected = new HashSet<IItem>(timeline.SelectedItems);
            var selected = currentSelected.Except(oldSelected).ToList();
            var deselected = oldSelected.Except(currentSelected).ToList();

            timelineSelectedItemsSnapshot[timeline] = currentSelected;

            foreach (var item in selected)
            {
                var itemId = ItemIdManager.GetOrCreateId(item);
                viewModel.LockItemLocally(itemId);
            }

            foreach (var item in deselected)
            {
                var itemId = ItemIdManager.GetOrCreateId(item);
                viewModel.UnlockItemLocally(itemId);
            }
        }

        private void SubscribeItem(IItem item, Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (itemSubscription.ContainsKey(item)) return;

            void handler(object? s, PropertyChangedEventArgs e)
            {
                if (getIsApplyingRemoteEventFunc()) return;

                var timelineIndex = viewModel.Scenes?.Timelines.IndexOf(timeline) ?? 0;
                var itemId = ItemIdManager.GetOrCreateId(item);

                if (!isItemEditableFunc(itemId)) return;

                if (e.PropertyName == nameof(IItem.Frame) || e.PropertyName == nameof(IItem.Layer) || e.PropertyName == nameof(IItem.Length))
                {
                    _ = eventSender.SendItemMovedThrottledAsync(itemId, timelineIndex, item.Frame, item.Length, item.Layer);
                }
                else
                {
                    _ = eventSender.SendItemUpdatedThrottledAsync(item, timelineIndex);
                }
            }

            item.PropertyChanged += handler;
            itemSubscription[item] = new ItemSubscription(item, handler);
        }

        private void UnsubscribeItem(IItem item)
        {
            if (itemSubscription.TryGetValue(item, out var sub))
            {
                item.PropertyChanged -= sub.Handler;
                itemSubscription.Remove(item);
            }
        }
    }
}
