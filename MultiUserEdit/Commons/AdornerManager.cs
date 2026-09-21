using MultiUserEdit.ViewModels;
using MultiUserEdit.Views.Adorners;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Views;

namespace MultiUserEdit.Commons
{
    public class AdornerManager
    {
        private AdornerLayer? adornerLayer;
        private TimelineCollaborationAdorner? collaborationAdorner;
        private Grid? timelineViewboxGrid;
        private CancellationTokenSource? attachCts;
        private DispatcherTimer? monitorTimer;
        private CollaborationSession? currentSession;
        private TimelineView? timelineViewReference;
        private Window? ownerWindowReference;
        private bool isAttaching;

        public void SetTimelineViewReference(TimelineView timelineView)
        {
            timelineViewReference = timelineView;
            if (currentSession != null)
                AttachAdorner(currentSession, ownerWindowReference);
        }

        public void AttachAdorner(MultiUserEditViewModel viewModel, Window? ownerWindow = null)
        {
            if (viewModel.CurrentSession != null)
            {
                AttachAdorner(viewModel.CurrentSession, ownerWindow);
            }
        }

        public void AttachAdorner(CollaborationSession session, Window? ownerWindow = null)
        {
            currentSession = session;
            if (ownerWindow != null)
            {
                ownerWindowReference = ownerWindow;
            }

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                attachCts?.Cancel();
                DetachAdornerInternal();

                attachCts = new CancellationTokenSource();
                _ = TryAttachAsync(session, attachCts.Token);

                StartMonitor();
            }, DispatcherPriority.Loaded);
        }

        public void DetachAdorner()
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StopMonitor();
                currentSession = null;
                attachCts?.Cancel();
                attachCts = null;
                DetachAdornerInternal();
            });
        }

        private void DetachAdornerInternal()
        {
            if (collaborationAdorner != null)
            {
                collaborationAdorner.Detach();
                if (adornerLayer != null)
                {
                    try { adornerLayer.Remove(collaborationAdorner); } catch { }
                }
            }

            collaborationAdorner = null;
            adornerLayer = null;
            timelineViewboxGrid = null;
        }

        private void StartMonitor()
        {
            if (monitorTimer != null) return;

            monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            monitorTimer.Tick += MonitorTick;
            monitorTimer.Start();
        }

        private void StopMonitor()
        {
            monitorTimer?.Stop();
            monitorTimer = null;
        }

        private void MonitorTick(object? sender, EventArgs e)
        {
            if (currentSession == null) { StopMonitor(); return; }

            if (collaborationAdorner != null && !IsAdornerStillShown())
            {
                DetachAdornerInternal();
            }

            if (collaborationAdorner == null && !isAttaching)
            {
                attachCts?.Cancel();
                attachCts = new CancellationTokenSource();
                _ = TryAttachAsync(currentSession, attachCts.Token);
            }
        }

        private bool IsAdornerStillShown()
        {
            var grid = timelineViewboxGrid;
            if (grid == null || PresentationSource.FromVisual(grid) == null) return false;

            var currentLayer = AdornerLayer.GetAdornerLayer(grid);
            if (currentLayer == null || currentLayer != adornerLayer) return false;

            if (grid.IsVisible) return true;

            return !EnumerateTimelineViews().Any(view => view.IsVisible);
        }

        private async Task TryAttachAsync(CollaborationSession session, CancellationToken token)
        {
            isAttaching = true;
            try
            {
                await TryAttachCoreAsync(session, token);
            }
            finally
            {
                isAttaching = false;
            }
        }

        private async Task TryAttachCoreAsync(CollaborationSession session, CancellationToken token)
        {
            for (int i = 0; i < 30 && !token.IsCancellationRequested; i++)
            {
                timelineViewboxGrid = TryGetViewboxGrid();
                if (timelineViewboxGrid != null)
                {
                    adornerLayer = AdornerLayer.GetAdornerLayer(timelineViewboxGrid);
                    if (adornerLayer != null)
                    {
                        collaborationAdorner = new TimelineCollaborationAdorner(timelineViewboxGrid, session);
                        adornerLayer.Add(collaborationAdorner);
                        return;
                    }
                }

                try
                {
                    await Task.Delay(500, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private Grid? TryGetViewboxGrid()
        {
            Grid? hiddenCandidate = null;

            foreach (var view in EnumerateTimelineViews())
            {
                var grid = VisualTreeHelperExtensions.FindElementByName<Grid>(view, "viewboxGrid")
                           ?? (view as ContentControl)?.Content as Grid;
                if (grid == null || AdornerLayer.GetAdornerLayer(grid) == null) continue;

                if (view.IsVisible) return grid;
                hiddenCandidate ??= grid;
            }

            return hiddenCandidate;
        }

        private IEnumerable<TimelineView> EnumerateTimelineViews()
        {
            var seen = new HashSet<TimelineView>();

            if (timelineViewReference != null
                && PresentationSource.FromVisual(timelineViewReference) != null
                && seen.Add(timelineViewReference))
            {
                yield return timelineViewReference;
            }

            var windows = new List<Window>();
            if (ownerWindowReference != null) windows.Add(ownerWindowReference);
            if (Application.Current != null)
            {
                foreach (Window window in Application.Current.Windows) windows.Add(window);
            }

            foreach (var window in windows.Distinct())
            {
                foreach (var view in FindAll<TimelineView>(window))
                {
                    if (PresentationSource.FromVisual(view) == null) continue;
                    if (seen.Add(view)) yield return view;
                }
            }
        }

        private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current is T match)
                {
                    yield return match;
                    continue;
                }

                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }
}