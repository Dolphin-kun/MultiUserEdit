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
            var parameter = character?.TachieCharacterParameter;
            if (parameter == null) return null;

            try
            {
                var declared = parameter is IFileItem fileItem ? fileItem.GetFiles() : CollectDeclaredPaths(parameter);

                foreach (var path in declared)
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

        public static IReadOnlyList<string> CollectDeclaredPaths(object? parameter)
        {
            if (parameter == null) return [];

            try
            {
                if (parameter is IFileItem fileItem)
                    return [.. fileItem.GetFiles().Where(path => !string.IsNullOrWhiteSpace(path))];

                var found = new List<string>();

                foreach (var property in parameter.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (property.PropertyType != typeof(string)) continue;
                    if (property.GetIndexParameters().Length > 0) continue;
                    if (!MediaFileResolver.IsFilePathProperty(property.Name)) continue;

                    if (property.GetValue(parameter) is not string value || string.IsNullOrWhiteSpace(value)) continue;
                    if (!File.Exists(value) && !Directory.Exists(value)) continue;

                    found.Add(value);
                }

                return found;
            }
            catch
            {
                return [];
            }
        }

        public static IReadOnlyList<string> EnumerateSharedFiles(string directory)
        {
            const int Limit = 4000;

            try
            {
                if (!Directory.Exists(directory)) return [];

                return [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => Settings.MultiUserEditSettings.Default.IsExtensionAllowed(path))
                    .Take(Limit)];
            }
            catch
            {
                return [];
            }
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
