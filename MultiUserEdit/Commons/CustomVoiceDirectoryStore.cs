using MultiUserEdit.Settings;
using System.IO;

namespace MultiUserEdit.Commons
{
    internal static class CustomVoiceDirectoryStore
    {
        private const char EntrySeparator = '\n';
        private const char FieldSeparator = '\t';

        public static string? Get(string characterName)
        {
            if (string.IsNullOrEmpty(characterName)) return null;

            return Load().TryGetValue(characterName, out var directory) && Directory.Exists(directory)
                ? directory
                : null;
        }

        public static void Set(string characterName, string directory)
        {
            if (string.IsNullOrEmpty(characterName) || string.IsNullOrEmpty(directory)) return;
            if (characterName.IndexOfAny([EntrySeparator, FieldSeparator]) >= 0) return;

            var entries = Load();
            entries[characterName] = directory;

            MultiUserEditSettings.Default.CustomVoiceDirectories = string.Join(
                EntrySeparator,
                entries.Select(entry => entry.Key + FieldSeparator + entry.Value));
        }

        private static Dictionary<string, string> Load()
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = MultiUserEditSettings.Default.CustomVoiceDirectories;
            if (string.IsNullOrEmpty(text)) return entries;

            foreach (var line in text.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(FieldSeparator, 2);
                if (fields.Length != 2) continue;
                if (fields[0].Length == 0 || fields[1].Length == 0) continue;

                entries[fields[0]] = fields[1];
            }

            return entries;
        }
    }
}
