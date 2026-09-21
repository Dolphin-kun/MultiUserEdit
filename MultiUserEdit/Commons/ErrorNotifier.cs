using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;

namespace MultiUserEdit.Commons
{
    internal static class ErrorNotifier
    {
        private static readonly ConcurrentDictionary<string, byte> notified = new();

        public static void NotifyOnce(string title, string message)
        {
            Debug.WriteLine($"[MultiUserEdit] {title}: {message}");

            if (!notified.TryAdd($"{title}\n{message}", 0)) return;

            Application.Current?.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(message, title, MessageBoxButton.OK));
        }
    }
}
