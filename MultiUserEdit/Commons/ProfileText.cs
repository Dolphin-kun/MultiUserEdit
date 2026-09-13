namespace MultiUserEdit.Commons
{
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
                var merged = string.Join(" ", lines.Skip(MaxDescriptionLines - 1));
                lines = [.. lines.Take(MaxDescriptionLines - 1), merged];
            }

            var result = string.Join("\n", lines);
            return result.Length > MaxDescriptionLength
                ? result[..MaxDescriptionLength].TrimEnd()
                : result;
        }

        public static string ToSingleLine(string? text) =>
            string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
    }
}
