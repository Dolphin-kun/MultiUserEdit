using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Models;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;

namespace MultiUserEdit.Views.Adorners
{
    public class TimelineCollaborationAdorner : Adorner
    {
        private readonly CollaborationSession _session;
        private readonly DispatcherTimer _refreshTimer;

        public TimelineCollaborationAdorner(UIElement adornedElement, CollaborationSession session)
            : base(adornedElement)
        {
            _session = session;
            IsHitTestVisible = false;

            _session.Participants.CollectionChanged += Participants_CollectionChanged;

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
            foreach (var p in _session.Participants)
                p.PropertyChanged -= Participant_PropertyChanged;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (_session.FirstOrDefaultTimeline == null) return;

            double zoom = SettingsBase<YMMSettings>.Default.TimelineZoom;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface("Yu Gothic UI");
            var localUserId = _session.LocalUserId;

            var localTimelineIndex = _session.Scenes?.Timelines.IndexOf(_session.FirstOrDefaultTimeline) ?? 0;

            // フレーム位置はタイムライン内容の座標なので、横スクロール量を引いて画面上の座標へ変換する
            double scrollX = GetTimelineHorizontalOffset();

            foreach (var participant in _session.Participants)
            {
                if (participant.UserId == localUserId) continue;
                if (participant.Status == UserStatus.Away) continue;
                if (participant.CurrentTimelineIndex != localTimelineIndex) continue;

                double x = participant.CurrentFrame * zoom / 100.0 - scrollX;
                if (x < 0 || x > RenderSize.Width) continue;

                var brush = new SolidColorBrush(participant.ThemeColor);
                brush.Freeze();
                var pen = new Pen(brush, 2);
                pen.Freeze();

                drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, RenderSize.Height));

                var text = new FormattedText(
                    participant.UserName,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    12,
                    brush,
                    dpi);

                drawingContext.DrawText(text, new Point(x + 4, 4));
            }
        }

        // YMM4のTimelineViewModelは横スクロール量を Viewport(Rect).X として公開しているが、
        // その型はプラグインから参照できないアセンブリにあるためリフレクションで読み取る。
        // ViewModelはシーンの切り替えで差し替わるため、インスタンスは保持せず毎回たどり直す
        private double GetTimelineHorizontalOffset()
        {
            try
            {
                // 装飾対象からその祖先へ向かって、Viewportを持つDataContext（TimelineViewModel）を探す
                DependencyObject? current = AdornedElement;
                while (current != null)
                {
                    if (current is FrameworkElement { DataContext: { } dataContext })
                    {
                        var reactive = dataContext.GetType().GetProperty("Viewport")?.GetValue(dataContext);
                        // ReactiveProperty<Rect> の実体型からValueを取る（インターフェース経由では見つからない）
                        var valueProperty = reactive?.GetType().GetProperty("Value");
                        if (valueProperty?.PropertyType == typeof(Rect))
                            return valueProperty.GetValue(reactive) is Rect rect ? rect.X : 0;
                    }

                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch { }

            return 0;
        }
    }
}