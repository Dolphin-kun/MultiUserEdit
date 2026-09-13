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
    public class FilesViewModel : Bindable
    {
        private readonly MultiUserEditViewModel? owner;

        public ObservableCollection<MediaFileInfo> Files { get; } = [];

        public ObservableCollection<TransferItemInfo> ActiveTransfers => owner?.ActiveTransfers ?? [];

        public bool HasActiveTransfers => ActiveTransfers.Count > 0;

        public string TransferHeaderText
        {
            get
            {
                var uploads = ActiveTransfers.Count(t => t.IsUpload);
                var downloads = ActiveTransfers.Count - uploads;

                if (uploads > 0 && downloads > 0) return $"送受信中 (送信 {uploads}件 / 受信 {downloads}件)";
                if (uploads > 0) return $"送信中 ({uploads}件)";
                if (downloads > 0) return $"受信中 ({downloads}件)";
                return "送受信はありません";
            }
        }

        public ICommand RefreshCommand { get; }
        public ICommand DeleteFileCommand { get; }
        public ICommand CleanFolderCommand { get; }
        public ICommand OpenFolderCommand { get; }

        public FilesViewModel(MultiUserEditViewModel? owner = null)
        {
            this.owner = owner;

            owner?.ActiveTransfers.CollectionChanged += OnActiveTransfersChanged;

            RefreshCommand = new ActionCommand((_) => true, ExecuteRefresh);
            DeleteFileCommand = new ActionCommand((_) => true, ExecuteDeleteFile);
            CleanFolderCommand = new ActionCommand((_) => true, ExecuteCleanFolder);
            OpenFolderCommand = new ActionCommand((_) => true, ExecuteOpenFolder);

            ExecuteRefresh(null);
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
                        LastModified = info.LastWriteTime
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] FilesViewModel Refresh failed: {ex.Message}");
            }
        }

        private void ExecuteDeleteFile(object? param)
        {
            if (param is not MediaFileInfo item) return;
            try
            {
                if (File.Exists(item.FilePath))
                {
                    File.Delete(item.FilePath);
                    Files.Remove(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] DeleteFile failed: {ex.Message}");
            }
        }

        private void ExecuteCleanFolder(object? param)
        {
            var confirmed = System.Windows.MessageBox.Show(
                "受信した素材ファイルをすべて削除します。\nよろしいですか？",
                "全消去の確認",
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
