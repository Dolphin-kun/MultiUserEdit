using MultiUserEdit.ViewModels;
using MultiUserEdit.Views.Adorners;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        private MultiUserEditViewModel? currentViewModel;
        private TimelineView? timelineViewReference;
        private Window? ownerWindowReference;

        public void SetTimelineViewReference(TimelineView timelineView)
        {
            timelineViewReference = timelineView;
            if (currentViewModel != null)
                AttachAdorner(currentViewModel, ownerWindowReference);
        }

        public void AttachAdorner(MultiUserEditViewModel viewModel, Window? ownerWindow = null)
        {
            currentViewModel = viewModel;
            if (ownerWindow != null)
            {
                ownerWindowReference = ownerWindow;
            }

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                attachCts?.Cancel();
                DetachAdornerInternal();

                attachCts = new CancellationTokenSource();
                _ = TryAttachAsync(viewModel, attachCts.Token);

                StartMonitor();
            }, DispatcherPriority.Loaded);
        }

        public void DetachAdorner()
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StopMonitor();
                currentViewModel = null;
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
            if (currentViewModel == null) { StopMonitor(); return; }

            if (collaborationAdorner != null && timelineViewboxGrid != null)
            {
                var currentLayer = AdornerLayer.GetAdornerLayer(timelineViewboxGrid);
                if (currentLayer == null || currentLayer != adornerLayer)
                {
                    DetachAdornerInternal();
                    attachCts?.Cancel();
                    attachCts = new CancellationTokenSource();
                    _ = TryAttachAsync(currentViewModel, attachCts.Token);
                }
            }

            if (collaborationAdorner == null && (attachCts == null || attachCts.IsCancellationRequested))
            {
                attachCts?.Cancel();
                attachCts = new CancellationTokenSource();
                _ = TryAttachAsync(currentViewModel, attachCts.Token);
            }
        }

        private async Task TryAttachAsync(MultiUserEditViewModel viewModel, CancellationToken token)
        {
            for (int i = 0; i < 30 && !token.IsCancellationRequested; i++)
            {
                timelineViewboxGrid = TryGetViewboxGrid();
                if (timelineViewboxGrid != null)
                {
                    adornerLayer = AdornerLayer.GetAdornerLayer(timelineViewboxGrid);
                    if (adornerLayer != null)
                    {
                        collaborationAdorner = new TimelineCollaborationAdorner(timelineViewboxGrid, viewModel);
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
            if (timelineViewReference != null)
            {
                var grid = VisualTreeHelperExtensions.FindElementByName<Grid>(timelineViewReference, "viewboxGrid")
                           ?? timelineViewReference.Content as Grid;
                if (grid != null) return grid;
            }

            if (ownerWindowReference != null)
            {
                var timelineView = VisualTreeHelperExtensions.FindVisualChildByTypeName(ownerWindowReference, "YukkuriMovieMaker.Views.TimelineView");
                if (timelineView != null)
                {
                    return VisualTreeHelperExtensions.FindElementByName<Grid>(timelineView, "viewboxGrid")
                           ?? (timelineView as ContentControl)?.Content as Grid;
                }
            }

            return null;
        }
    }
}       