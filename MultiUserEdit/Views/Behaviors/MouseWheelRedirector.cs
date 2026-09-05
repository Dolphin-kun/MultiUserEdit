using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiUserEdit.Views.Behaviors
{
    /// <summary>
    /// TextBox や PasswordBox は自前でスクロール可能なため、ScrollViewer 内に置くと
    /// マウスホイールを吸ってしまい外側のスクロールが動かなくなる。
    /// PreviewMouseWheel からこれを呼び、親要素へイベントを転送して外側をスクロールさせる。
    /// </summary>
    internal static class MouseWheelRedirector
    {
        /// <summary>
        /// 入れ子のScrollViewer用。これ以上スクロールできない向きのホイールだけ親へ渡す。
        /// （渡さないと、内側が端に達した後もホイールが吸われて外側が動かない）
        /// </summary>
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
