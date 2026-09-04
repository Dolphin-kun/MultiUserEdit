using System.Windows;
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
