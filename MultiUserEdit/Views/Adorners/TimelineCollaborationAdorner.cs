using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.ViewModels;
using YukkuriMovieMaker.Views;

namespace MultiUserEdit.Views.Adorners
{
    public class TimelineCollaborationAdorner : Adorner
    {
        private const double AwayOpacity = 0.35;
        private static readonly TimeSpan OperatingDisplayDuration = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CanvasOffsetLifetime = TimeSpan.FromSeconds(1);

        private Point? canvasOffset;
        private DateTime canvasOffsetMeasuredAt;

        private readonly CollaborationSession _session;
        private readonly DispatcherTimer _refreshTimer;
        private readonly Typeface _typeface = new("Yu Gothic UI");

        public TimelineCollaborationAdorner(UIElement adornedElement, CollaborationSession session)
            : base(adornedElement)
        {
            _session = session;
            IsHitTestVisible = false;

            _session.Participants.CollectionChanged += Participants_CollectionChanged;
            _session.OperatingItemsChanged += InvalidateVisual;

            foreach (var p in _session.Participants)
            {
                p.PropertyChanged -= Participant_PropertyChanged;
                p.PropertyChanged += Participant_PropertyChanged;
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _refreshTimer.Tick += (s, e) => InvalidateVisual();
            _refreshTimer.Start();
        }

        private void Participants_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (Participant p in e.NewItems)
                {
                    p.PropertyChanged -= Participant_PropertyChanged;
                    p.PropertyChanged += Participant_PropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (Participant p in e.OldItems)
                    p.PropertyChanged -= Participant_PropertyChanged;
            }

            InvalidateVisual();
        }

        private void Participant_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(Participant.CurrentFrame)
                or nameof(Participant.CurrentTimelineIndex)
                or nameof(Participant.Status))
            {
                InvalidateVisual();
            }
        }

        public void Detach()
        {
            _refreshTimer.Stop();
            _session.Participants.CollectionChanged -= Participants_CollectionChanged;
            _session.OperatingItemsChanged -= InvalidateVisual;
            foreach (var p in _session.Participants)
                p.PropertyChanged -= Participant_PropertyChanged;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (_session.Scenes == null) return;

            var timelineViewModel = FindTimelineViewModel();
            var displayedTimelineIndex = GetDisplayedTimelineIndex(timelineViewModel);
            if (displayedTimelineIndex < 0) return;

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            try
            {
                DrawItemOperators(drawingContext, dpi, displayedTimelineIndex, timelineViewModel);
                DrawPlayheads(drawingContext, dpi, displayedTimelineIndex, timelineViewModel);
            }
            finally
            {
                drawingContext.Pop();
            }
        }

        private void DrawPlayheads(DrawingContext drawingContext, double dpi, int displayedTimelineIndex, TimelineViewModel? timelineViewModel)
        {
            var zoom = SettingsBase<YMMSettings>.Default.TimelineZoom;
            var scrollX = timelineViewModel?.Viewport.Value.X ?? 0;
            var localUserId = _session.LocalUserId;

            foreach (var participant in _session.Participants)
            {
                if (participant.UserId == localUserId) continue;
                if (participant.Status == UserStatus.Disconnected) continue;
                if (participant.CurrentTimelineIndex != displayedTimelineIndex) continue;

                var x = participant.CurrentFrame * zoom / 100.0 - scrollX;
                if (x < 0 || x > RenderSize.Width) continue;

                var opacity = participant.Status == UserStatus.Away ? AwayOpacity : 1.0;
                var brush = CreateBrush(participant.ThemeColor, opacity);
                var pen = new Pen(brush, 2);
                pen.Freeze();

                drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, RenderSize.Height));

                var label = participant.Status == UserStatus.Away ? $"{participant.UserName} (離席中)" : participant.UserName;
                drawingContext.DrawText(CreateText(label, 12, brush, dpi), new Point(x + 4, 4));
            }
        }

        private void DrawItemOperators(DrawingContext drawingContext, double dpi, int displayedTimelineIndex, TimelineViewModel? timelineViewModel)
        {
            if (timelineViewModel == null || _session.OperatingItems.Count == 0) return;

            var now = DateTime.UtcNow;
            var localUserId = _session.LocalUserId;
            var active = new Dictionary<Guid, OperatingItem>();
            foreach (var (itemId, operation) in _session.OperatingItems)
            {
                if (operation.UserId == localUserId) continue;
                if (operation.TimelineIndex != displayedTimelineIndex) continue;
                if (now - operation.LastActivity > OperatingDisplayDuration) continue;
                active[itemId] = operation;
            }

            if (active.Count == 0) return;

            var offset = GetCanvasOffset(timelineViewModel, active);
            if (offset == null) return;

            var viewport = timelineViewModel.Viewport.Value;

            foreach (var itemViewModel in timelineViewModel.Items)
            {
                if (itemViewModel.Item is not IItem item) continue;
                if (!active.TryGetValue(ItemIdManager.GetOrCreateId(item), out var operation)) continue;

                var owner = _session.Participants.FirstOrDefault(p => p.UserId == operation.UserId);
                if (owner == null) continue;

                var bounds = new Rect(
                    offset.Value.X + itemViewModel.Left - viewport.X,
                    offset.Value.Y + itemViewModel.Top - viewport.Y,
                    Math.Max(0, itemViewModel.Width),
                    Math.Max(0, itemViewModel.Height));
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                var brush = CreateBrush(owner.ThemeColor, 1.0);
                var pen = new Pen(brush, 2);
                pen.Freeze();
                drawingContext.DrawRectangle(null, pen, bounds);

                var text = CreateText($"{owner.UserName} が操作中", 11, GetReadableForeground(owner.ThemeColor), dpi);
                var tagHeight = text.Height + 2;
                var tagTop = bounds.Top - tagHeight >= 0 ? bounds.Top - tagHeight : bounds.Top;
                var tag = new Rect(bounds.Left, tagTop, text.Width + 8, tagHeight);

                drawingContext.DrawRectangle(brush, null, tag);
                drawingContext.DrawText(text, new Point(tag.Left + 4, tag.Top + 1));
            }
        }

        private Point? GetCanvasOffset(TimelineViewModel timelineViewModel, Dictionary<Guid, OperatingItem> operating)
        {
            var now = DateTime.UtcNow;
            if (canvasOffset != null && now - canvasOffsetMeasuredAt < CanvasOffsetLifetime) return canvasOffset;

            var viewport = timelineViewModel.Viewport.Value;
            var root = (DependencyObject?)VisualTreeHelperExtensions.FindVisualParent<TimelineView>(AdornedElement) ?? AdornedElement;

            Point? measured = null;
            foreach (var view in EnumerateItemViews(root))
            {
                if (view.DataContext is not TimelineItemViewModel { Item: IItem item } itemViewModel) continue;

                Point actual;
                try
                {
                    actual = view.TransformToVisual(AdornedElement).Transform(new Point(0, 0));
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                measured = new Point(actual.X - (itemViewModel.Left - viewport.X), actual.Y - (itemViewModel.Top - viewport.Y));

                if (!operating.ContainsKey(ItemIdManager.GetOrCreateId(item))) break;
            }

            if (measured != null)
            {
                canvasOffset = measured;
                canvasOffsetMeasuredAt = now;
            }

            return canvasOffset;
        }

        private static IEnumerable<FrameworkElement> EnumerateItemViews(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current is TimelineItemView itemView)
                {
                    yield return itemView;
                    continue;
                }

                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        private TimelineViewModel? FindTimelineViewModel()
        {
            DependencyObject? current = AdornedElement;
            while (current != null)
            {
                if (current is FrameworkElement { DataContext: TimelineViewModel timelineViewModel }) return timelineViewModel;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private int GetDisplayedTimelineIndex(TimelineViewModel? timelineViewModel)
        {
            var timelines = _session.Scenes?.Timelines;
            if (timelines == null) return -1;

            var shownItem = timelineViewModel?.Items.FirstOrDefault()?.Item;
            if (shownItem != null)
            {
                for (var i = 0; i < timelines.Count; i++)
                {
                    if (timelines[i].Items.Contains(shownItem)) return i;
                }
            }

            return _session.FirstOrDefaultTimeline is { } fallback ? timelines.IndexOf(fallback) : 0;
        }

        private FormattedText CreateText(string text, double size, Brush brush, double dpi) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, size, brush, dpi);

        private static SolidColorBrush CreateBrush(Color color, double opacity)
        {
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush GetReadableForeground(Color background)
        {
            var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luminance > 0.6 ? Brushes.Black : Brushes.White;
        }
    }
}
