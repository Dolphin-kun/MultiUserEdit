using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiUserEdit.Views.Behaviors
{
    internal static class MouseWheelRedirector
    {
        public static void RedirectAtEdge(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer) return;

            var canScrollUp = e.Delta > 0 && scrollViewer.VerticalOffset > 0;
            var canScrollDown = e.Delta < 0 && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
            if (canScrollUp || canScrollDown) return;

            Redirect(sender, e);
        }

        public static void Redirect(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || sender is not UIElement element) return;

            e.Handled = true;

            if (VisualTreeHelper.GetParent(element) is not UIElement parent) return;

            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = element
            });
        }
    }
}
