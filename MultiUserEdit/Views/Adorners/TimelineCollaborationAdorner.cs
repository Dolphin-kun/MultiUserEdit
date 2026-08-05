using MultiUserEdit.Commons.Models;
using MultiUserEdit.ViewModels;
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
        private readonly MultiUserEditViewModel _viewModel;
        private readonly DispatcherTimer _refreshTimer;

        public TimelineCollaborationAdorner(UIElement adornedElement, MultiUserEditViewModel viewModel)
            : base(adornedElement)
        {
            _viewModel = viewModel;
            IsHitTestVisible = false;

            _viewModel.Participants.CollectionChanged += Participants_CollectionChanged;

            foreach (var p in _viewModel.Participants)
            {
                p.PropertyChanged -= Participant_PropertyChanged;
                p.PropertyChanged += Participant_PropertyChanged;
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
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
            _viewModel.Participants.CollectionChanged -= Participants_CollectionChanged;
            foreach (var p in _viewModel.Participants)
                p.PropertyChanged -= Participant_PropertyChanged;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (_viewModel.FirstOrDefaultTimeline == null) return;

            double zoom = SettingsBase<YMMSettings>.Default.TimelineZoom;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface("Yu Gothic UI");
            var localUserId = _viewModel.LocalUserId;

            var localTimelineIndex = _viewModel.Scenes?.Timelines.IndexOf(_viewModel.FirstOrDefaultTimeline) ?? 0;

            foreach (var participant in _viewModel.Participants)
            {
                if (participant.UserId == localUserId) continue;
                if (participant.Status == UserStatus.Away) continue;
                if (participant.CurrentTimelineIndex != localTimelineIndex) continue;

                double x = participant.CurrentFrame * zoom / 100.0;

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
    }
}          