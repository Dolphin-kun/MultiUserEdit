using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.ViewModels
{
    public class FilesViewModel : Bindable, IDisposable
    {
        private readonly MultiUserEditViewModel? owner;

        public ObservableCollection<MediaFileInfo> Files { get; } = [];

        private static readonly ObservableCollection<TransferItemInfo> NoTransfers = [];
        private ObservableCollection<TransferItemInfo>? subscribedTransfers;
        private readonly DisposeCollector disposer = new();

        public ObservableCollection<TransferItemInfo> ActiveTransfers => subscribedTransfers ?? NoTransfers;

        public bool HasActiveTransfers => ActiveTransfers.Count > 0;

        public string TransferHeaderText
        {
            get
            {
                var uploads = ActiveTransfers.Count(t => t.IsUpload);
                var downloads = ActiveTransfers.Count - uploads;

                if (uploads > 0 && downloads > 0) return string.Format(Texts.FilesBoth, uploads, downloads);
                if (uploads > 0) return string.Format(Texts.FilesSending, uploads);
                if (downloads > 0) return string.Format(Texts.FilesReceiving, downloads);
                return Texts.FilesIdle;
            }
        }

        public ICommand RefreshCommand { get; }
        public ICommand DeleteFileCommand { get; }
        public ICommand CleanFolderCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand CancelTransferCommand { get; }

        public FilesViewModel(MultiUserEditViewModel? owner = null)
        {
            this.owner = owner;

            owner?.PropertyChanged += OnOwnerPropertyChanged;
            ResubscribeTransfers();
            owner?.SentFilesChanged += OnFilesChanged;
            owner?.FileTransferCompleted += OnFileReceived;
            disposer.CollectAction(nameof(owner), () =>
            {
                owner?.PropertyChanged -= OnOwnerPropertyChanged;
                owner?.SentFilesChanged -= OnFilesChanged;
                owner?.FileTransferCompleted -= OnFileReceived;
                subscribedTransfers?.CollectionChanged -= OnActiveTransfersChanged;
                subscribedTransfers = null;
            });

            RefreshCommand = new ActionCommand((_) => true, ExecuteRefresh);
            DeleteFileCommand = new ActionCommand((_) => true, ExecuteDeleteFile);
            CleanFolderCommand = new ActionCommand((_) => true, ExecuteCleanFolder);
            OpenFolderCommand = new ActionCommand((_) => true, ExecuteOpenFolder);
            CancelTransferCommand = new ActionCommand((_) => true, ExecuteCancelTransfer);

            ExecuteRefresh(null);
        }

        public void Dispose()
        {
            disposer.Dispose();
            GC.SuppressFinalize(this);
        }

        private void OnOwnerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MultiUserEditViewModel.ActiveTransfers))
                ResubscribeTransfers();
        }

        private void ResubscribeTransfers()
        {
            var current = owner?.CurrentSession?.ActiveTransfers;
            if (ReferenceEquals(current, subscribedTransfers)) return;

            subscribedTransfers?.CollectionChanged -= OnActiveTransfersChanged;
            current?.CollectionChanged += OnActiveTransfersChanged;
            Set(ref subscribedTransfers, current, nameof(ActiveTransfers), nameof(HasActiveTransfers), nameof(TransferHeaderText));
        }

        private void OnFileReceived(string transferId, string path) => OnFilesChanged();

        private void OnFilesChanged()
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => ExecuteRefresh(null));
        }

        private void OnActiveTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasActiveTransfers));
            OnPropertyChanged(nameof(TransferHeaderText));
        }

        public void ExecuteRefresh(object? param)
        {
            Files.Clear();
            try
            {
                foreach (var sent in owner?.GetSentFiles() ?? [])
                {
                    Files.Add(new MediaFileInfo
                    {
                        FileName = Path.GetFileName(sent.FullPath),
                        FilePath = sent.FullPath,
                        SizeBytes = sent.SizeBytes,
                        LastModified = sent.SentAt,
                        IsSent = true,
                        WasTransferred = sent.WasTransferred
                    });
                }

                var dir = FileTransferManager.GetSaveDirectory();
                if (!Directory.Exists(dir)) return;

                var filePaths = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (var path in filePaths)
                {
                    if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                    var info = new FileInfo(path);
                    Files.Add(new MediaFileInfo
                    {
                        FileName = Path.GetRelativePath(dir, info.FullName),
                        FilePath = info.FullName,
                        SizeBytes = info.Length,
                        LastModified = info.LastWriteTime,
                        OwnerName = FileTransferManager.GetOwnerName(info.FullName)
                    });
                }
            }
            catch
            {
            }
        }

        private void ExecuteCancelTransfer(object? param)
        {
            if (param is not TransferItemInfo transfer || !transfer.CanCancel) return;

            var confirmed = System.Windows.MessageBox.Show(
                string.Format(Texts.CancelReceiveConfirm, transfer.DisplayName),
                Texts.CancelReceiveTitle,
                System.Windows.MessageBoxButton.OKCancel) == System.Windows.MessageBoxResult.OK;

            if (!confirmed) return;

            owner?.CancelIncomingTransfer(transfer.TransferId);
        }

        private void ExecuteDeleteFile(object? param)
        {
            if (param is not MediaFileInfo item || !item.CanDelete) return;
            try
            {
                if (File.Exists(item.FilePath))
                {
                    File.Delete(item.FilePath);
                    Files.Remove(item);
                }
            }
            catch
            {
            }
        }

        private void ExecuteCleanFolder(object? param)
        {
            var confirmed = System.Windows.MessageBox.Show(
                Texts.ClearAllConfirm,
                Texts.ClearAllTitle,
                System.Windows.MessageBoxButton.OKCancel) == System.Windows.MessageBoxResult.OK;

            if (!confirmed) return;

            FileTransferManager.DeleteAllFiles();
            ExecuteRefresh(null);
        }

        private void ExecuteOpenFolder(object? param)
        {
            try
            {
                var dir = FileTransferManager.GetSaveDirectory();
                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }
    }
}
