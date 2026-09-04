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
        private readonly Dictionary<Timeline, TimelineSubscription> timelineSubscriptions = [];

        public void RegisterTimeline(Timeline timeline, MultiUserEditViewModel viewModel)
        {
            if (timeline == null) return;

            // ラムダ式を都度生成して -= / += すると、生成のたびに別デリゲートになるため -= が空振りし、
            // RegisterTimelineを呼ぶたび（パネルの表示切り替えやシーン切り替えのたび）に購読が
            // 際限なく積み上がっていた。実際に登録したデリゲートを保持し、確実に解除してから登録し直す。
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
                videoInfoHandler = (s, e) => OnVideoInfoPropertyChanged(timeline, e, viewModel);
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
                // スロットル状態はGuid基準で保持しているため、アイテム削除時に明示的に破棄しないと
                // セッションを使い続けるほど際限なく蓄積してしまう
                eventSender.ClearItemThrottleState(itemId);
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
                    _ = eventSender.SendItemMovedThrottledAsync(itemId, timelineIndex, item.Frame, item.Length, item.Layer);
                }
                else
                {
                    // 変更前後でJSON全体を比較して同一なら送信を省く、という重複排除は
                    // プロパティ変更のたびにフルシリアライズが走りUIスレッドを圧迫していた
                    // （ドラッグ中は1秒間に何十回も発火しうる）。送信自体はスロットリング済みのため、
                    // ここでは比較せず素通しする。
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
