using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Windows;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;

namespace MultiUserEdit.Commons
{
    internal static class FontAvailabilityChecker
    {
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);
        private const int MaxListed = 10;

        private static readonly HashSet<string> PropertyNameSet =
            new(["Font", "FontName", "FontFamily"], StringComparer.OrdinalIgnoreCase);

        private static readonly Lock gate = new();
        private static readonly Dictionary<Guid, (HashSet<string> Fonts, string SenderName)> pending = [];
        private static readonly HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
        private static int scheduleGeneration;

        public static void NotifyMissingFonts(Guid itemId, string itemJson, string? senderName)
        {
            try
            {
                if (!TryCollectFontNames(itemJson, out var fonts)) return;

                lock (gate)
                {
                    if (pending.TryGetValue(itemId, out var queued) && queued.Fonts.SetEquals(fonts)) return;

                    pending[itemId] = (fonts, senderName ?? string.Empty);

                    var generation = ++scheduleGeneration;
                    _ = Task.Delay(QuietPeriod).ContinueWith(
                        _ => Application.Current?.Dispatcher.InvokeAsync(() => Flush(generation)),
                        TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Font check failed: {ex.Message}");
            }
        }

        private static void Flush(int generation)
        {
            List<(HashSet<string> Fonts, string SenderName)> batch;
            lock (gate)
            {
                if (generation != scheduleGeneration) return;

                batch = [.. pending.Values];
                pending.Clear();
            }

            var installed = GetInstalledFontNames();
            if (installed.Count == 0) return;

            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var senders = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (fonts, senderName) in batch)
            {
                foreach (var font in fonts)
                {
                    if (installed.Contains(font)) continue;

                    missing.Add(font);
                    if (!string.IsNullOrEmpty(senderName)) senders.Add(senderName);
                }
            }

            List<string> newlyMissing;
            lock (gate)
            {
                newlyMissing = [.. missing.Where(reported.Add)];
            }

            if (newlyMissing.Count == 0) return;

            var who = senders.Count == 1 ? senders.First() : "共同編集相手";
            var list = string.Join("\n", newlyMissing.Take(MaxListed).Select(name => "・" + name));
            if (newlyMissing.Count > MaxListed) list += $"\n ほか {newlyMissing.Count - MaxListed} 件";

            MessageBox.Show(
                $"{who} が使用しているフォントがこの環境にありません。\n\n{list}\n\n代わりのフォントで表示されるため、見た目が異なります。",
                "フォントが見つかりません",
                MessageBoxButton.OK);
        }

        private static bool TryCollectFontNames(string itemJson, out HashSet<string> names)
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasFontProperty = false;

            var root = JObject.Parse(itemJson);
            foreach (var property in root.DescendantsAndSelf().OfType<JProperty>())
            {
                if (!PropertyNameSet.Contains(property.Name)) continue;
                if (property.Value.Type != JTokenType.String) continue;

                hasFontProperty = true;

                var value = (string?)property.Value;
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
            }

            return hasFontProperty;
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
