namespace MultiUserEdit.Commons.Models
{
    internal enum FileGroupKind
    {
        Image,
        Video,
        Audio,
        Other,
        Custom,
    }

    internal static class FileExtensionCatalog
    {
        public record Group(FileGroupKind Kind, IReadOnlyList<string> Extensions);

        public static readonly IReadOnlyList<Group> Groups =
        [
            new(FileGroupKind.Image, [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".dds", ".heic", ".psd", ".svg"]),
            new(FileGroupKind.Video, [".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".ogv", ".asf"]),
            new(FileGroupKind.Audio, [".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".mid", ".midi"]),
            new(FileGroupKind.Other, [".ymmp", ".ymmt", ".lab", ".txt", ".csv", ".tsv", ".srt", ".sub", ".sbv"]),
        ];

        public static IEnumerable<string> AllExtensions =>
            Groups.SelectMany(group => group.Extensions);

        public static IReadOnlyList<string> DefaultEnabled =>
        [
            .. Groups.Where(group => group.Kind != FileGroupKind.Other).SelectMany(group => group.Extensions),
            ".ymmp", ".ymmt", ".lab",
        ];

        public static string DefaultCsv => string.Join(", ", DefaultEnabled);

        public static string NameOf(FileGroupKind kind) => kind switch
        {
            FileGroupKind.Image => Texts.Image,
            FileGroupKind.Video => Texts.Video,
            FileGroupKind.Audio => Texts.Audio,
            FileGroupKind.Other => Texts.Ymm4AndOther,
            _ => Texts.AddedFormats,
        };

        public static FileGroupKind GetKindForFile(string filePath) =>
            GetKind(Normalize(System.IO.Path.GetExtension(filePath)) ?? string.Empty);

        public static FileGroupKind GetKind(string extension) =>
            Groups.FirstOrDefault(group => group.Extensions.Contains(extension))?.Kind ?? FileGroupKind.Custom;

        public static string? Normalize(string? extension)
        {
            var text = extension?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text)) return null;

            if (!text.StartsWith('.')) text = "." + text;
            return text.Length > 1 ? text : null;
        }
    }
}
