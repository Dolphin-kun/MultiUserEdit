using Newtonsoft.Json.Linq;
using System.IO;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal readonly record struct LocalMediaState(IReadOnlyList<string> Paths)
    {
        public static LocalMediaState Empty { get; } = new([]);
    }

    internal static class LocalMediaGuard
    {
        private const string VoiceCacheProperty = "VoiceCache";

        private static readonly HashSet<string> PathPropertyNames =
            new(MediaFileResolver.PropertyNames, StringComparer.OrdinalIgnoreCase);

        public static string StripVoiceCache(string itemJson)
        {
            if (itemJson.IndexOf(VoiceCacheProperty, StringComparison.Ordinal) < 0) return itemJson;

            try
            {
                var root = JObject.Parse(itemJson);
                var found = root.Descendants()
                    .OfType<JProperty>()
                    .Where(property => property.Name == VoiceCacheProperty)
                    .ToList();

                if (found.Count == 0) return itemJson;

                foreach (var property in found) property.Remove();

                return root.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return itemJson;
            }
        }

        public static LocalMediaState Capture(IItem? item)
        {
            if (item == null) return LocalMediaState.Empty;

            try
            {
                return new LocalMediaState(MediaFileResolver.GetReferencedPaths(item));
            }
            catch
            {
                return LocalMediaState.Empty;
            }
        }

        public static string Apply(string itemJson, IItem? item) => Apply(itemJson, Capture(item));

        public static string Apply(string itemJson, LocalMediaState state)
        {
            var ownPaths = state.Paths
                .Where(path => !string.IsNullOrEmpty(path) && !IsReceivedCopy(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ownByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in ownPaths)
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name)) ownByName.TryAdd(name, path);
            }

            JObject root;
            try
            {
                root = JObject.Parse(itemJson);
            }
            catch
            {
                return itemJson;
            }

            var changed = false;
            var blanks = new List<JProperty>();

            foreach (var property in Enumerate(root).ToList())
            {
                if (!PathPropertyNames.Contains(property.Name)) continue;

                if (property.Value.Type == JTokenType.Null
                    || (property.Value.Type == JTokenType.String && string.IsNullOrEmpty((string?)property.Value)))
                {
                    blanks.Add(property);
                    continue;
                }

                if (property.Value.Type != JTokenType.String) continue;

                var incoming = (string?)property.Value;
                if (string.IsNullOrEmpty(incoming) || !IsReceivedCopy(incoming)) continue;

                if (ownByName.TryGetValue(Path.GetFileName(incoming), out var original)
                    && !string.Equals(original, incoming, StringComparison.OrdinalIgnoreCase))
                {
                    property.Value = original;
                    changed = true;
                }
            }

            foreach (var property in blanks)
            {
                property.Remove();
                changed = true;
            }

            return changed ? root.ToString(Newtonsoft.Json.Formatting.None) : itemJson;
        }

        private static bool IsReceivedCopy(string path)
        {
            try
            {
                var shared = FileTransferManager.GetSaveDirectory();
                return !string.IsNullOrEmpty(shared)
                    && Path.GetFullPath(path).StartsWith(Path.GetFullPath(shared), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<JProperty> Enumerate(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        yield return property;

                        foreach (var nested in Enumerate(property.Value)) yield return nested;
                    }
                    break;

                case JArray array:
                    foreach (var element in array)
                    {
                        foreach (var nested in Enumerate(element)) yield return nested;
                    }
                    break;
            }
        }
    }
}
