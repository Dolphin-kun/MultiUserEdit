using System.Windows;
using MultiUserEdit.Settings;

namespace MultiUserEdit.Commons
{
    internal static class DirectTransferChoice
    {
        private static readonly Lock gate = new();
        private static Task<bool>? asking;

        public static event Action<bool>? Decided;

        public static bool? Current
        {
            get
            {
                var settings = MultiUserEditSettings.Default;
                if (!settings.UseDirectTransfer) return false;
                return settings.DirectTransferAsked ? true : null;
            }
        }

        public static Task<bool> AskAsync()
        {
            lock (gate)
            {
                if (Current is bool known) return Task.FromResult(known);
                if (asking != null) return asking;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return Task.FromResult(false);
                asking = dispatcher.InvokeAsync(Ask).Task;
                return asking;
            }
        }

        private static bool Ask()
        {
            var useDirect = MessageBox.Show(
                Texts.DirectTransferAskMessage,
                Texts.DirectTransferAskTitle,
                MessageBoxButton.YesNo) != MessageBoxResult.No;

            var settings = MultiUserEditSettings.Default;
            settings.UseDirectTransfer = useDirect;
            settings.DirectTransferAsked = true;
            lock (gate) asking = null;

            try
            {
                Decided?.Invoke(useDirect);
            }
            catch
            {
            }
            return useDirect;
        }
    }
}
