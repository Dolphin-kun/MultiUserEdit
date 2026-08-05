using System.IO;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class MediaFileResolver
    {
        private static readonly string[] PropertyNames = ["FilePath", "PsdPath", "ImagePath", "File", "Path"];

        public static string ResolveLocalTempPath(string originalFilePath)
        {
            if (string.IsNullOrEmpty(originalFilePath)) return string.Empty;
            var fileName = Path.GetFileName(originalFilePath);
            var saveDir = FileTransferManager.GetSaveDirectory();
            return Path.Combine(saveDir, fileName);
        }

        public static string? GetFilePath(IItem item)
        {
            if (item is VideoItem videoItem) return videoItem.FilePath;
            if (item is AudioItem audioItem) return audioItem.FilePath;
            if (item is ImageItem imageItem) return imageItem.FilePath;

            var type = item.GetType();
            foreach (var propName in PropertyNames)
            {
                var prop = type.GetProperty(propName);
                if (prop != null && prop.GetValue(item) is string val && !string.IsNullOrEmpty(val))
                    return val;
            }
            return null;
        }

        public static bool SetFilePath(IItem item, string newFilePath)
        {
            if (item is VideoItem videoItem)
            {
                videoItem.FilePath = newFilePath;
                return true;
            }
            if (item is AudioItem audioItem)
            {
                audioItem.FilePath = newFilePath;
                return true;
            }
            if (item is ImageItem imageItem)
            {
                imageItem.FilePath = newFilePath;
                return true;
            }

            var type = item.GetType();
            bool success = false;
            foreach (var propName in PropertyNames)
            {
                var prop = type.GetProperty(propName);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(item, newFilePath);
                    success = true;
                }
            }
            return success;
        }
    }
}
