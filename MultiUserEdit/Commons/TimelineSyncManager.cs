using MultiUserEdit.Commons.Models;
using MultiUserEdit.ViewModels;
using System.ComponentModel;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal class TimelineSyncManager(EditEventSender eventSender, Func<bool> getIsApplyingRemoteEventFunc)
    {
        private readonly EditEventSender eventSender = eventSender;
        private readonly Func<bool> getIsApplyingRemoteEventFunc = getIsApplyingRemoteEventFunc;

        private readonly Dictionary<IItem, ItemSubscription> itemSubscription = [];
        private readonly Dictionary<Timeline, HashSet<IItem>> timelineItemSnapshot = [];
        private readonly Dictionary<Timeline, HashSet<IItem>> timelineSelectedItemsSnapshot = [];
        private readonly Dictionary<Guid, string> itemJsonCache = [];

        public void RegisterTimeline(Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (timeline == null) return;

            timeline.PropertyChanged -= (s, e) => OnTimelinePropertyChanged(timeline, e, viewModel);
            timeline.PropertyChanged += (s, e) => OnTimelinePropertyChanged(timeline, e, viewModel);

            if (timeline.VideoInfo != null)
            {
                timeline.VideoInfo.PropertyChanged -= (s, e) => OnVideoInfoPropertyChanged(timeline, e, viewModel);
                timeline.VideoInfo.PropertyChanged += (s, e) => OnVideoInfoPropertyChanged(timeline, e, viewModel);
            }

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

            foreach (var item in timeline.Items)
            {
                UnsubscribeItem(item);
            }

            timelineItemSnapshot.Remove(timeline);
            timelineSelectedItemsSnapshot.Remove(timeline);
        }

        public void UpdateItemJsonCache(IItem item)
        {
            try
            {
                var itemId = ItemIdManager.GetOrCreateId(item);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(item, ItemSerializerOptions.Default);
                itemJsonCache[itemId] = json;
            }
            catch { }
        }

        private void OnTimelinePropertyChanged(Timeline timeline, PropertyChangedEventArgs e, MultiUserEditViewModel viewModel)
        {
            if (getIsApplyingRemoteEventFunc()) return;

            var timelineIndex = viewModel.Scenes?.Timelines.IndexOf(timeline) ?? 0;

            if (e.PropertyName == nameof(Timeline.Items))
            {
                OnItemsCollectionChanged(timeline, timelineIndex, viewModel);
            }
            else if (e.PropertyName == nameof(Timeline.SelectedItems))
            {
                OnSelectedItemsCollectionChanged(timeline, viewModel);
            }
            else if (e.PropertyName == nameof(Timeline.Name))
            {
                _ = eventSender.SendSceneRenamedAsync(timelineIndex, timeline.Name);
            }
        }

        private void OnVideoInfoPropertyChanged(Timeline timeline, PropertyChangedEventArgs e, MultiUserEditViewModel viewModel)
        {
            if (getIsApplyingRemoteEventFunc()) return;

            var timelineIndex = viewModel.Scenes?.Timelines.IndexOf(timeline) ?? 0;
            var videoInfo = timeline.VideoInfo;
            if (videoInfo != null)
            {
                _ = eventSender.SendVideoInfoUpdatedAsync(timelineIndex, videoInfo.Width, videoInfo.Height, videoInfo.FPS, videoInfo.Hz);
            }
        }

        private void OnItemsCollectionChanged(Timeline timeline, int timelineIndex, MultiUserEditViewModel viewModel)
        {
            if (!timelineItemSnapshot.TryGetValue(timeline, out var oldItems))
            {
                oldItems = [];
            }

            var currentItems = new HashSet<IItem>(timeline.Items);

            foreach (var added in currentItems.Except(oldItems))
            {
                SubscribeItem(added, timeline, viewModel);
                _ = eventSender.SendItemAddedAsync(added, added.Frame, added.Layer, timelineIndex);
            }

            foreach (var removed in oldItems.Except(currentItems))
            {
                UnsubscribeItem(removed);
                var itemId = ItemIdManager.GetOrCreateId(removed);
                _ = eventSender.SendItemRemovedAsync(itemId, timelineIndex);
            }

            timelineItemSnapshot[timeline] = currentItems;
        }

        private void OnSelectedItemsCollectionChanged(Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (!timelineSelectedItemsSnapshot.TryGetValue(timeline, out var oldSelected))
            {
                oldSelected = [];
            }

            var currentSelected = new HashSet<IItem>(timeline.SelectedItems);

            foreach (var item in currentSelected.Except(oldSelected))
            {
                var itemId = ItemIdManager.GetOrCreateId(item);
                viewModel.LockItemLocally(itemId);
            }

            foreach (var item in oldSelected.Except(currentSelected))
            {
                var itemId = ItemIdManager.GetOrCreateId(item);
                viewModel.UnlockItemLocally(itemId);
            }

            timelineSelectedItemsSnapshot[timeline] = currentSelected;
        }

        private void SubscribeItem(IItem item, Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (itemSubscription.ContainsKey(item)) return;

            void handler(object? s, PropertyChangedEventArgs e)
            {
                if (getIsApplyingRemoteEventFunc()) return;

                var timelineIndex = viewModel.Scenes?.Timelines.IndexOf(timeline) ?? 0;
                var itemId = ItemIdManager.GetOrCreateId(item);

                if (e.PropertyName == nameof(IItem.Frame) || e.PropertyName == nameof(IItem.Layer) || e.PropertyName == nameof(IItem.Length))
                {
                    _ = eventSender.SendItemMovedAsync(itemId, timelineIndex, item.Frame, item.Length, item.Layer);
                }
                else
                {
                    var currentJson = Newtonsoft.Json.JsonConvert.SerializeObject(item, ItemSerializerOptions.Default);
                    if (itemJsonCache.TryGetValue(itemId, out var cachedJson) && cachedJson == currentJson)
                    {
                        return;
                    }

                    itemJsonCache[itemId] = currentJson;
                    _ = eventSender.SendItemUpdatedAsync(item, timelineIndex);
                }
            }

            item.PropertyChanged += handler;
            itemSubscription[item] = new ItemSubscription(item, handler);

            UpdateItemJsonCache(item);
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
