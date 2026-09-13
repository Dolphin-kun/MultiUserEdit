using MultiUserEdit.ViewModels;
using System.IO;
using System.Windows;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal static class TransferWaiter
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

        public static void WhenFilesReady(MultiUserEditViewModel viewModel, IReadOnlyList<string> fileNames, Action action, TimeSpan? timeout = null)
        {
            if (fileNames.Count == 0)
            {
                action();
                return;
            }

            var remaining = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
            var finished = false;

            void onCompleted(string transferId, string savedPath)
            {
                lock (remaining)
                {
                    if (finished) return;

                    remaining.RemoveWhere(name => Matches(savedPath, name));
                    if (remaining.Count > 0) return;

                    finished = true;
                }

                viewModel.FileTransferCompleted -= onCompleted;

                Application.Current?.Dispatcher.InvokeAsync(() => viewModel.ExecuteRemoteAction(action));
            }

            viewModel.FileTransferCompleted += onCompleted;

            foreach (var fileName in fileNames)
            {
                var savePath = MediaFileResolver.ResolveLocalTempPath(fileName);
                if (File.Exists(savePath)) onCompleted(string.Empty, savePath);
            }

            if (timeout == null) return;

            _ = Task.Delay(timeout.Value).ContinueWith(_ =>
            {
                lock (remaining)
                {
                    if (finished) return;
                    finished = true;
                }

                viewModel.FileTransferCompleted -= onCompleted;
                Application.Current?.Dispatcher.InvokeAsync(() => viewModel.ExecuteRemoteAction(action));
            }, TaskScheduler.Default);
        }

        private static bool Matches(string savedPath, string name)
        {
            var normalized = TachieFileResolver.ToLocalSeparators(name);

            return savedPath.EndsWith(Path.DirectorySeparatorChar + normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(savedPath), normalized, StringComparison.OrdinalIgnoreCase);
        }
    }
}
