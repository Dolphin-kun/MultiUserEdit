using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;

namespace MultiUserEdit.Commons
{
    internal static class FontAvailabilityChecker
    {
        private static readonly string[] FontPropertyNames = ["Font", "FontName", "FontFamily"];

        private static readonly HashSet<string> PropertyNameSet =
            new(FontPropertyNames, StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, byte> reported =
            new(StringComparer.OrdinalIgnoreCase);

        public static void NotifyMissingFonts(string itemJson, string? senderName)
        {
            try
            {
                var missing = GetMissingFonts(itemJson);
                if (missing.Count == 0) return;

                var who = string.IsNullOrEmpty(senderName) ? "共同編集相手" : senderName;
                ErrorNotifier.NotifyOnce(
                    "フォントが見つかりません",
                    $"{who} が使用しているフォントがこの環境にありません。\n\n" +
                    string.Join("\n", missing.Select(name => "・" + name)) +
                    "\n\n代わりのフォントで表示されるため、見た目が異なります。");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Font check failed: {ex.Message}");
            }
        }

        private static List<string> GetMissingFonts(string itemJson)
        {
            var used = CollectFontNames(itemJson);
            if (used.Count == 0) return [];

            var installed = GetInstalledFontNames();
            if (installed.Count == 0) return [];

            return [.. used
                .Where(name => !installed.Contains(name))
                .Where(name => reported.TryAdd(name, 0))];
        }

        private static HashSet<string> CollectFontNames(string itemJson)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var root = JObject.Parse(itemJson);
            foreach (var property in root.DescendantsAndSelf().OfType<JProperty>())
            {
                if (!PropertyNameSet.Contains(property.Name)) continue;
                if (property.Value.Type != JTokenType.String) continue;

                var value = (string?)property.Value;
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
            }

            return names;
        }

        private static HashSet<string> GetInstalledFontNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var settings = SettingsBase<FontSettings>.Default;
                foreach (var font in settings.SystemFonts) names.Add(font.FontName);
                foreach (var font in settings.CustomFonts) names.Add(font.FontName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Font list unavailable: {ex.Message}");
            }

            return names;
        }
    }
}
