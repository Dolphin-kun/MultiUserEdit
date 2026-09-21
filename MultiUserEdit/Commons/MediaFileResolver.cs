using Newtonsoft.Json.Linq;
using System.IO;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal readonly record struct SharedFile(string FullPath, string Name, bool ShouldTransfer = true);

    internal static class MediaFileResolver
    {
        internal static readonly string[] PropertyNames = ["FilePath", "PsdPath", "ImagePath", "TachiEPath", "PsdFilePath", "ImageFilePath", "SourcePath", "EnableLayersFilePath", "Directory", "File", "Path"];

        private static readonly HashSet<string> PathPropertyNames = new(PropertyNames, StringComparer.OrdinalIgnoreCase);

        [ThreadStatic]
        private static string? serializationBaseDirectory;
        [ThreadStatic]
        private static string? serializationCharacterName;

        public static string ToPortableName(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;

            return TachieFileResolver.ToPortableName(serializationBaseDirectory, serializationCharacterName, filePath)
                ?? Path.GetFileName(filePath);
        }

        public static string ResolveLocalTempPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var relative = TachieFileResolver.IsSafeRelativePath(name)
                ? TachieFileResolver.ToLocalSeparators(name)
                : Path.GetFileName(name);

            return Path.Combine(FileTransferManager.GetSaveDirectory(), relative);
        }

        public static bool RequiresRealMediaContainer(IItem item) => item is VideoItem or AudioItem;

        public static bool RequiresRealMediaContainer(Type itemType) =>
            typeof(VideoItem).IsAssignableFrom(itemType) || typeof(AudioItem).IsAssignableFrom(itemType);

        public static IReadOnlyList<string> GetMissingFileNames(IReadOnlyList<string>? mediaFileNames, string? tachieBaseDirectory = null)
        {
            if (mediaFileNames == null || mediaFileNames.Count == 0) return [];

            return [.. mediaFileNames
                .Where(name => !string.IsNullOrEmpty(name)
                            && TachieFileResolver.ResolveLocalPath(tachieBaseDirectory, name) == null
                            && !File.Exists(ResolveLocalTempPath(name))
                            && IsReceivable(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        private static bool IsReceivable(string name)
        {
            if (Settings.MultiUserEditSettings.Default.IsExtensionAllowed(name)) return true;

            var extension = Path.GetExtension(name);
            ErrorNotifier.NotifyOnce(
                "素材を受け取れませんでした",
                $"拡張子 [{extension}] のファイルは共有設定で許可されていないため受け取れません。\n" +
                "設定 → ファイル → 共有可能なファイル形式 から許可してください。");

            return false;
        }

        public static IReadOnlyList<string> GetMissingFileNames(IReadOnlyList<string>? mediaFileNames, Type itemType, string? tachieBaseDirectory = null) =>
            RequiresRealMediaContainer(itemType) ? [] : GetMissingFileNames(mediaFileNames, tachieBaseDirectory);

        public static IReadOnlyList<string> GetReferencedPaths(IItem item)
        {
            var paths = new List<string>();

            if (item is IFileItem fileItem)
                paths.AddRange(fileItem.GetFiles().Where(f => !string.IsNullOrEmpty(f)));
            else if (GetFilePath(item) is { Length: > 0 } legacyPath)
                paths.Add(legacyPath);

            return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        public static IReadOnlyList<string> GetFilePaths(IItem item)
        {
            return [.. GetReferencedPaths(item).Where(File.Exists)];
        }

        public static IReadOnlyList<string> GetTransferableFilePaths(IItem item)
        {
            return [.. GetFilePaths(item).Where(fp => ShouldTransfer(item, fp))];
        }

        private static bool ShouldTransfer(IItem item, string filePath) =>
            CharacterResolver.GetCharacter(item) == null ||
            !Path.GetExtension(filePath).Equals(".psd", StringComparison.OrdinalIgnoreCase);

        public static string ResolveJsonFileReferences(string itemJson, IItem item, IReadOnlyList<string>? mediaFileNames) =>
            ResolveJsonFileReferences(itemJson, RequiresRealMediaContainer(item), mediaFileNames, TachieFileResolver.GetBaseDirectory(item));

        public static string ResolveJsonFileReferences(string itemJson, Type itemType, IReadOnlyList<string>? mediaFileNames) =>
            ResolveJsonFileReferences(itemJson, itemType, mediaFileNames, GetTachieBaseDirectoryFromJson(itemJson));

        public static string ResolveJsonFileReferences(string itemJson, Type itemType, IReadOnlyList<string>? mediaFileNames, string? tachieBaseDirectory) =>
            ResolveJsonFileReferences(itemJson, RequiresRealMediaContainer(itemType), mediaFileNames, tachieBaseDirectory);

        private static string ResolveJsonFileReferences(string itemJson, bool requiresRealContainer, IReadOnlyList<string>? mediaFileNames, string? tachieBaseDirectory, bool keepMissingPaths = false)
        {
            var targets = new HashSet<string>(
                mediaFileNames?.Where(name => !string.IsNullOrEmpty(name)) ?? [],
                StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0 && tachieBaseDirectory == null) return itemJson;

            return EditFilePathProperties(itemJson, targets, property =>
            {
                if (property.Value.Type != JTokenType.String) return false;

                var fileName = (string?)property.Value;
                if (string.IsNullOrEmpty(fileName)) return false;

                if (Path.IsPathRooted(fileName)) return false;

                if (TachieFileResolver.ResolveLocalPath(tachieBaseDirectory, fileName) is { } characterPath)
                {
                    property.Value = characterPath;
                    return false;
                }

                if (!targets.Contains(fileName)) return tachieBaseDirectory != null;

                var resolvedPath = ResolveLocalTempPath(fileName);

                if (File.Exists(resolvedPath))
                {
                    property.Value = resolvedPath;
                    return false;
                }

                if (keepMissingPaths)
                {
                    property.Value = resolvedPath;
                    return false;
                }

                if (requiresRealContainer)
                {
                    property.Value = JValue.CreateNull();
                    return false;
                }

                return true;
            });
        }

        public static string? GetCharacterNameFromJson(string itemJson)
        {
            try
            {
                return (string?)JObject.Parse(itemJson)["CharacterName"];
            }
            catch
            {
                return null;
            }
        }

        public static string? GetTachieBaseDirectoryFromJson(string itemJson) =>
            TachieFileResolver.GetBaseDirectory(GetCharacterNameFromJson(itemJson));

        private static string EditFilePathProperties(string itemJson, IReadOnlySet<string> referencedValues, Func<JProperty, bool> edit)
        {
            JObject root;
            try
            {
                root = JObject.Parse(itemJson);
            }
            catch
            {
                return itemJson;
            }

            var removals = new List<JProperty>();
            foreach (var property in EnumerateFilePathProperties(root, referencedValues).ToList())
            {
                if (edit(property)) removals.Add(property);
            }

            if (removals.Count == 0 && !root.HasValues) return itemJson;

            foreach (var property in removals) property.Remove();

            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static IEnumerable<JProperty> EnumerateFilePathProperties(JToken token, IReadOnlySet<string> referencedValues)
        {
            switch (token)
            {
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        if (IsFileReference(property, referencedValues))
                        {
                            yield return property;
                            continue;
                        }

                        foreach (var nested in EnumerateFilePathProperties(property.Value, referencedValues)) yield return nested;
                    }
                    break;

                case JArray array:
                    foreach (var element in array)
                    {
                        foreach (var nested in EnumerateFilePathProperties(element, referencedValues)) yield return nested;
                    }
                    break;
            }
        }

        private static bool IsFileReference(JProperty property, IReadOnlySet<string> referencedValues)
        {
            if (PathPropertyNames.Contains(property.Name)) return true;
            if (property.Value.Type != JTokenType.String) return false;

            var value = (string?)property.Value;
            if (string.IsNullOrEmpty(value)) return false;

            return TachieFileResolver.IsTachieName(value) || referencedValues.Contains(value);
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

        public static string SerializeWithNullPath(IItem item, Type? itemType = null)
        {
            return itemType != null
                ? Newtonsoft.Json.JsonConvert.SerializeObject(item, itemType, ItemSerializerOptions.NullPath)
                : Newtonsoft.Json.JsonConvert.SerializeObject(item, ItemSerializerOptions.NullPath);
        }

        public static (string itemJson, IReadOnlyList<SharedFile> files) SerializeForSync(IItem item, Type? itemType = null, Func<string, bool>? filter = null)
        {
            var character = CharacterResolver.GetCharacter(item);
            serializationBaseDirectory = TachieFileResolver.GetBaseDirectory(character);
            serializationCharacterName = character?.Name;
            try
            {
                return SerializeForSyncCore(item, itemType, filter);
            }
            finally
            {
                serializationBaseDirectory = null;
                serializationCharacterName = null;
            }
        }

        public static (string characterJson, IReadOnlyList<SharedFile> files) SerializeCharacterForSync(Character character)
        {
            var baseDirectory = TachieFileResolver.GetBaseDirectory(character);
            serializationBaseDirectory = baseDirectory;
            serializationCharacterName = character.Name;
            try
            {
                var referenced = ((IFileItem)character).GetFiles()
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var files = referenced
                    .Where(File.Exists)
                    .Select(path => new SharedFile(path, ToPortableName(path)))
                    .ToList();
                files.AddRange(GetTachieSiblingImages(files));

                var characterJson = Newtonsoft.Json.JsonConvert.SerializeObject(character, ItemSerializerOptions.CharacterNullPath);
                characterJson = ReplaceFullPathsInJson(characterJson, files);

                var skipped = referenced
                    .Where(path => !File.Exists(path) && !Directory.Exists(path))
                    .ToList();
                if (skipped.Count > 0) characterJson = ClearFileReferences(characterJson, skipped);

                return (characterJson, files);
            }
            finally
            {
                serializationBaseDirectory = null;
                serializationCharacterName = null;
            }
        }

        public static string ResolveCharacterJson(string characterJson, IReadOnlyList<string>? mediaFileNames, string baseDirectory) =>
            ResolveJsonFileReferences(characterJson, requiresRealContainer: false, mediaFileNames, baseDirectory, keepMissingPaths: true);

        private static (string itemJson, IReadOnlyList<SharedFile> files) SerializeForSyncCore(IItem item, Type? itemType, Func<string, bool>? filter)
        {
            var referenced = GetReferencedPaths(item);
            var shared = referenced
                .Where(fp => File.Exists(fp) && (!ShouldTransfer(item, fp) || filter == null || filter(fp)))
                .Select(fp => new SharedFile(fp, ToPortableName(fp), ShouldTransfer(item, fp)))
                .ToList();
            shared.AddRange(GetTachieSiblingImages(shared));

            var itemJson = SerializeWithNullPath(item, itemType);
            itemJson = ReplaceFullPathsInJson(itemJson, shared);

            var skipped = referenced
                .Except(shared.Select(file => file.FullPath), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (skipped.Count > 0) itemJson = ClearFileReferences(itemJson, skipped);

            return (itemJson, shared);
        }

        private static readonly TimeSpan SiblingCacheLifetime = TimeSpan.FromSeconds(5);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime CachedAt, string[] Files)> siblingCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static List<SharedFile> GetTachieSiblingImages(List<SharedFile> files)
        {
            var siblings = new List<SharedFile>();
            var known = new HashSet<string>(files.Select(file => file.FullPath), StringComparer.OrdinalIgnoreCase);
            var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files.ToList())
            {
                if (!file.ShouldTransfer) continue;
                if (!TachieFileResolver.IsTachieName(file.Name)) continue;

                var directory = Path.GetDirectoryName(file.FullPath);
                if (string.IsNullOrEmpty(directory) || !visitedDirectories.Add(directory)) continue;

                foreach (var candidate in GetImageFiles(directory))
                {
                    if (!known.Add(candidate)) continue;
                    if (!Settings.MultiUserEditSettings.Default.IsExtensionAllowed(candidate)) continue;

                    var portableName = ToPortableName(candidate);
                    if (string.IsNullOrEmpty(portableName)) continue;

                    siblings.Add(new SharedFile(candidate, portableName));
                }
            }

            return siblings;
        }

        private static string[] GetImageFiles(string directory)
        {
            var now = DateTime.UtcNow;
            if (siblingCache.TryGetValue(directory, out var cached) && now - cached.CachedAt < SiblingCacheLifetime)
                return cached.Files;

            string[] images;
            try
            {
                var fileExtensions = YukkuriMovieMaker.Plugin.SettingsBase<YukkuriMovieMaker.Settings.FileSettings>.Default.FileExtensions;
                images = Directory.Exists(directory)
                    ? [.. Directory.EnumerateFiles(directory)
                        .Where(path => fileExtensions.GetFileType(path).HasFlag(YukkuriMovieMaker.Settings.FileType.画像))]
                    : [];
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiUserEdit] Sibling image lookup failed: {ex.Message}");
                images = [];
            }

            siblingCache[directory] = (now, images);
            return images;
        }

        private static string ReplaceFullPathsInJson(string itemJson, List<SharedFile> files)
        {
            if (files.Count == 0) return itemJson;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!string.IsNullOrEmpty(file.FullPath) && !string.IsNullOrEmpty(file.Name))
                    map[file.FullPath] = file.Name;
            }

            if (map.Count == 0) return itemJson;

            try
            {
                var root = JObject.Parse(itemJson);
                var changed = false;

                foreach (var token in root.DescendantsAndSelf().OfType<JValue>())
                {
                    if (token.Type != JTokenType.String) continue;
                    if (token.Value is not string text || text.Length == 0) continue;
                    if (!map.TryGetValue(text, out var portableName)) continue;

                    token.Value = portableName;
                    changed = true;
                }

                return changed ? root.ToString(Newtonsoft.Json.Formatting.None) : itemJson;
            }
            catch
            {
                return itemJson;
            }
        }

        private static string ClearFileReferences(string itemJson, List<string> filePaths)
        {
            if (filePaths.Count == 0) return itemJson;

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in filePaths)
            {
                targets.Add(filePath);
                targets.Add(ToPortableName(filePath));
            }

            return EditFilePathProperties(itemJson, targets, property =>
                property.Value.Type == JTokenType.String &&
                (string?)property.Value is { Length: > 0 } value &&
                targets.Contains(value));
        }

    }
}
