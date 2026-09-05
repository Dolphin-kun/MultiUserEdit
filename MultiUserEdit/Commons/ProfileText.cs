namespace MultiUserEdit.Commons
{
    /// <summary>
    /// 自己紹介文はユーザー情報ダイアログのヘッダーに表示されるため、
    /// 文字数と行数を制限しないとレイアウトが崩れる。
    /// 入力時・受信時の両方でここを通して正規化する。
    /// </summary>
    internal static class ProfileText
    {
        public const int MaxDescriptionLength = 140;
        public const int MaxDescriptionLines = 3;

        public const string DescriptionHint = "140文字・3行まで（超えた分は自動でまとめられます）";

        public static string NormalizeDescription(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var lines = text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            if (lines.Count > MaxDescriptionLines)
            {
                // 上限を超えた行は最終行へ空白でつないでまとめる（行を捨てて内容を失わせない）
                var merged = string.Join(" ", lines.Skip(MaxDescriptionLines - 1));
                lines = [.. lines.Take(MaxDescriptionLines - 1), merged];
            }

            var result = string.Join("\n", lines);
            return result.Length > MaxDescriptionLength
                ? result[..MaxDescriptionLength].TrimEnd()
                : result;
        }

        /// <summary>改行を空白へ置き換える。高さを増やしたくない表示箇所で使う。</summary>
        public static string ToSingleLine(string? text) =>
            string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
    }
}
