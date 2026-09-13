namespace MultiUserEdit.Commons.Models
{
    internal static class FileExtensionCatalog
    {
        public record Group(string Name, IReadOnlyList<string> Extensions);

        public static readonly IReadOnlyList<Group> Groups =
        [
            new("画像", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".dds", ".heic", ".psd", ".svg"]),
            new("動画", [".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".ogv", ".asf"]),
            new("音声", [".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".mid", ".midi"]),
            new("YMM4・その他", [".ymmp", ".ymmt", ".lab", ".txt", ".csv", ".tsv", ".srt", ".sub", ".sbv"]),
        ];

        public static IEnumerable<string> AllExtensions =>
            Groups.SelectMany(group => group.Extensions);

        public static IReadOnlyList<string> DefaultEnabled =>
        [
            .. Groups.Where(group => group.Name != "YMM4・その他").SelectMany(group => group.Extensions),
            ".ymmp", ".ymmt", ".lab",
        ];

        public static string DefaultCsv => string.Join(", ", DefaultEnabled);

        public const string CustomGroupName = "追加した形式";

        public static string GetGroupName(string extension) =>
            Groups.FirstOrDefault(group => group.Extensions.Contains(extension))?.Name ?? CustomGroupName;

        public static string? Normalize(string? extension)
        {
            var text = extension?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text)) return null;

            if (!text.StartsWith('.')) text = "." + text;
            return text.Length > 1 ? text : null;
        }
    }
}
