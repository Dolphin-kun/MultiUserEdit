using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.ViewModels
{
    public class FilesViewModel : Bindable
    {
        public ObservableCollection<MediaFileInfo> Files { get; } = [];

        public ICommand RefreshCommand { get; }
        public ICommand DeleteFileCommand { get; }
        public ICommand CleanFolderCommand { get; }
        public ICommand OpenFolderCommand { get; }

        public FilesViewModel()
        {
            RefreshCommand = new ActionCommand((_) => true, ExecuteRefresh);
            DeleteFileCommand = new ActionCommand((_) => true, ExecuteDeleteFile);
            CleanFolderCommand = new ActionCommand((_) => true, ExecuteCleanFolder);
            OpenFolderCommand = new ActionCommand((_) => true, ExecuteOpenFolder);

            ExecuteRefresh(null);
        }

        public void ExecuteRefresh(object? param)
        {
            Files.Clear();
            try
            {
                var dir = FileTransferManager.GetSaveDirectory();
                if (!Directory.Exists(dir)) return;

                var filePaths = Directory.GetFiles(dir);
                foreach (var path in filePaths)
                {
                    if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                    var info = new FileInfo(path);
                    Files.Add(new MediaFileInfo
                    {
                        FileName = info.Name,
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
            FileTransferManager.CleanUpTempFiles();
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
