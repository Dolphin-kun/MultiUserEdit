using System.IO;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class TachieFileResolver
    {
        private const string Prefix = "_tachie";

        public static string? GetBaseDirectory(IItem item) =>
            GetBaseDirectory(CharacterResolver.GetCharacter(item));

        public static string? GetBaseDirectory(string? characterName) =>
            GetBaseDirectory(CharacterResolver.FindCharacter(characterName ?? string.Empty));

        public static string? GetBaseDirectory(Character? character)
        {
            if (character?.TachieCharacterParameter is not IFileItem fileItem) return null;

            try
            {
                foreach (var path in fileItem.GetFiles())
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    if (Directory.Exists(path)) return path;

                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory;
                }
            }
            catch { }

            return null;
        }

        public static string? ToPortableName(string? baseDirectory, string? characterName, string filePath)
        {
            var relative = ToRelativePath(baseDirectory, filePath);
            if (relative == null) return null;

            var owner = SanitizeSegment(characterName);
            return relative.Length == 0 ? $"{Prefix}/{owner}" : $"{Prefix}/{owner}/{relative}";
        }

        public static bool IsTachieName(string? portableName) =>
            portableName != null && (portableName == Prefix || portableName.StartsWith(Prefix + "/", StringComparison.Ordinal));

        public static string? GetRelativePart(string? portableName)
        {
            if (!IsTachieName(portableName)) return null;

            var segments = portableName!.Split('/');
            return segments.Length <= 2 ? string.Empty : string.Join('/', segments.Skip(2));
        }

        public static string GetSharedRoot(string? characterName) =>
            Path.Combine(FileTransferManager.GetSaveDirectory(), Prefix, SanitizeSegment(characterName));

        public static string? ResolveLocalPath(string? baseDirectory, string portableName)
        {
            if (string.IsNullOrEmpty(baseDirectory)) return null;

            var relative = GetRelativePart(portableName);
            if (relative == null) return null;

            try
            {
                if (relative.Length == 0) return Directory.Exists(baseDirectory) ? baseDirectory : null;
                if (!IsSafeRelativePath(relative)) return null;

                var path = Path.Combine(baseDirectory, ToLocalSeparators(relative));
                return File.Exists(path) || Directory.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ToRelativePath(string? baseDirectory, string filePath)
        {
            if (string.IsNullOrEmpty(baseDirectory) || string.IsNullOrEmpty(filePath)) return null;

            try
            {
                var fullPath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar);
                var root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar);

                if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return string.Empty;

                var rootWithSeparator = root + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) return null;

                return fullPath[rootWithSeparator.Length..].Replace(Path.DirectorySeparatorChar, '/');
            }
            catch
            {
                return null;
            }
        }

        public static bool IsSafeRelativePath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            if (Path.IsPathRooted(relativePath)) return false;
            if (relativePath.Contains(':')) return false;

            return relativePath
                .Split('/', '\\')
                .All(segment => segment.Length > 0 && segment != "." && segment != "..");
        }

        public static string ToLocalSeparators(string relativePath) =>
            relativePath.Replace('/', Path.DirectorySeparatorChar);

        private static string SanitizeSegment(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "_";

            var sanitized = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
            return sanitized is "." or ".." ? "_" : sanitized;
        }
    }
}
