namespace MultiUserEdit.Commons.Models
{
    /// <summary>
    /// 共有を許可する拡張子の候補一覧。
    ///
    /// YMM4自体は「対応拡張子の一覧」を持っておらず、読み込み系プラグイン
    /// (MediaFoundation / FFmpeg / WIC / PSD / SVG / MP3 / DirectShow) に
    /// 順番に読み込ませて成功したものを採用する方式のため、SDKから正確な一覧は取得できない。
    /// そのため同梱プラグインが扱える代表的な形式をここに列挙している。
    /// 一覧に無い形式はユーザーが個別に追加できる。
    /// </summary>
    internal static class FileExtensionCatalog
    {
        public record Group(string Name, IReadOnlyList<string> Extensions);

        public static readonly IReadOnlyList<Group> Groups =
        [
            new("画像", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".dds", ".heic", ".psd", ".svg"]),
            new("動画", [".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".ogv", ".asf"]),
            new("音声", [".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".mid", ".midi"]),
            // .lab は音声アイテムが口パク用に参照するため、音声と一緒に転送できる必要がある
            new("YMM4・その他", [".ymmp", ".ymmt", ".lab", ".txt", ".csv", ".tsv", ".srt", ".sub", ".sbv"]),
        ];

        public static IEnumerable<string> AllExtensions =>
            Groups.SelectMany(group => group.Extensions);

        /// <summary>既定で有効にする拡張子。字幕・テキスト系は口パク用の .lab だけ有効にする。</summary>
        public static IReadOnlyList<string> DefaultEnabled =>
        [
            .. Groups.Where(group => group.Name != "YMM4・その他").SelectMany(group => group.Extensions),
            ".ymmp", ".ymmt", ".lab",
        ];

        public static string DefaultCsv => string.Join(", ", DefaultEnabled);

        /// <summary>一覧のどのグループに属するか。未知の拡張子は「追加した形式」。</summary>
        public const string CustomGroupName = "追加した形式";

        public static string GetGroupName(string extension) =>
            Groups.FirstOrDefault(group => group.Extensions.Contains(extension))?.Name ?? CustomGroupName;

        /// <summary>先頭のドットを補い、小文字に揃える。空文字なら null。</summary>
        public static string? Normalize(string? extension)
        {
            var text = extension?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(text)) return null;

            if (!text.StartsWith('.')) text = "." + text;
            return text.Length > 1 ? text : null;
        }
    }
}
