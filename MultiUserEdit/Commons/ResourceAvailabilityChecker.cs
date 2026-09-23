using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Windows;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;

namespace MultiUserEdit.Commons
{
    internal static class ResourceAvailabilityChecker
    {
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);
        private const int MaxListed = 10;

        private static readonly HashSet<string> FontPropertyNames =
            new(["Font", "FontName", "FontFamily"], StringComparer.OrdinalIgnoreCase);

        private static readonly Lock gate = new();
        private static readonly Dictionary<Guid, PendingItem> pending = [];
        private static readonly HashSet<string> reportedFonts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> reportedPlugins = new(StringComparer.OrdinalIgnoreCase);
        private static int scheduleGeneration;

        public static Action<Guid, string[], string[]>? MissingReported;

        private class PendingItem
        {
            public HashSet<string> Fonts = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Types = new(StringComparer.Ordinal);
            public Guid SenderId;
            public string SenderName = string.Empty;
        }

        public static void Reset()
        {
            lock (gate)
            {
                pending.Clear();
                reportedFonts.Clear();
                reportedPlugins.Clear();
                scheduleGeneration++;
            }
        }

        public static void Notify(Guid itemId, Guid senderId, string? senderName, string? itemJson, string? itemTypeName)
        {
            try
            {
                var entry = new PendingItem
                {
                    SenderId = senderId,
                    SenderName = senderName ?? string.Empty
                };

                if (!string.IsNullOrWhiteSpace(itemTypeName)) entry.Types.Add(itemTypeName);
                if (!string.IsNullOrWhiteSpace(itemJson)) Collect(itemJson, entry);

                if (entry.Fonts.Count == 0 && entry.Types.Count == 0) return;

                lock (gate)
                {
                    if (pending.TryGetValue(itemId, out var queued)
                        && queued.Fonts.SetEquals(entry.Fonts)
                        && queued.Types.SetEquals(entry.Types))
                    {
                        return;
                    }

                    pending[itemId] = entry;

                    var generation = ++scheduleGeneration;
                    _ = Task.Delay(QuietPeriod).ContinueWith(
                        _ => Application.Current?.Dispatcher.InvokeAsync(() => Flush(generation)),
                        TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Resource check failed: {ex.Message}");
            }
        }

        private static void Flush(int generation)
        {
            List<PendingItem> batch;
            lock (gate)
            {
                if (generation != scheduleGeneration) return;

                batch = [.. pending.Values];
                pending.Clear();
            }

            var installedFonts = GetInstalledFontNames();

            var missingFonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingPlugins = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var senders = new HashSet<string>(StringComparer.Ordinal);
            var bySender = new Dictionary<Guid, (HashSet<string> Fonts, HashSet<string> Plugins)>();

            foreach (var entry in batch)
            {
                var found = false;

                if (!bySender.TryGetValue(entry.SenderId, out var owned))
                {
                    owned = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    bySender[entry.SenderId] = owned;
                }

                if (installedFonts.Count > 0)
                {
                    foreach (var font in entry.Fonts)
                    {
                        if (installedFonts.Contains(font)) continue;
                        missingFonts.Add(font);
                        owned.Fonts.Add(font);
                        found = true;
                    }
                }

                foreach (var typeName in entry.Types)
                {
                    if (ItemTypeResolver.Resolve(typeName) != null) continue;

                    var displayName = ToDisplayName(typeName);
                    missingPlugins.Add(displayName);
                    owned.Plugins.Add(displayName);
                    found = true;
                }

                if (!found) continue;

                if (!string.IsNullOrEmpty(entry.SenderName)) senders.Add(entry.SenderName);
            }

            List<string> newFonts;
            List<string> newPlugins;
            lock (gate)
            {
                newFonts = [.. missingFonts.Where(reportedFonts.Add)];
                newPlugins = [.. missingPlugins.Where(reportedPlugins.Add)];
            }

            if (newFonts.Count == 0 && newPlugins.Count == 0) return;

            foreach (var (senderId, owned) in bySender)
            {
                string[] fonts = [.. newFonts.Where(owned.Fonts.Contains)];
                string[] plugins = [.. newPlugins.Where(owned.Plugins.Contains)];
                MissingReported?.Invoke(senderId, fonts, plugins);
            }

            var who = senders.Count == 1 ? senders.First() : "共同編集相手";
            var sections = new List<string>();

            if (newPlugins.Count > 0)
            {
                sections.Add($"【プラグイン】\n{Format(newPlugins)}\n該当するアイテムや効果は、この環境では表示されません。");
            }

            if (newFonts.Count > 0)
            {
                sections.Add($"【フォント】\n{Format(newFonts)}\n代わりのフォントで表示されるため、見た目が異なります。");
            }

            var title = newPlugins.Count > 0
                ? newFonts.Count > 0 ? "プラグイン・フォントが見つかりません" : "プラグインが見つかりません"
                : "フォントが見つかりません";

            MessageBox.Show(
                $"{who} が使用しているものが、この環境にありません。\n\n{string.Join("\n\n", sections)}",
                title,
                MessageBoxButton.OK);
        }

        public static void NotifyReportedByPeer(string peerName, IReadOnlyList<string> fonts, IReadOnlyList<string> plugins)
        {
            if (fonts.Count == 0 && plugins.Count == 0) return;

            var sections = new List<string>();
            if (plugins.Count > 0) sections.Add($"【プラグイン】\n{Format(plugins)}");
            if (fonts.Count > 0) sections.Add($"【フォント】\n{Format(fonts)}");

            MessageBox.Show(
                $"{peerName} さんの環境に、あなたが使用しているものがありません。\n\n{string.Join("\n\n", sections)}\n"
                + "相手の画面では、表示が異なるか、アイテムが表示されていません。",
                "相手の環境にないものがあります",
                MessageBoxButton.OK);
        }

        private static string Format(IReadOnlyList<string> names)
        {
            var list = string.Join("\n", names.Take(MaxListed).Select(name => "・" + name));
            if (names.Count > MaxListed) list += $"\n ほか {names.Count - MaxListed} 件";
            return list;
        }

        private static string ToDisplayName(string typeName)
        {
            var parts = typeName.Split(',');
            var fullName = parts[0].Trim();
            var assembly = parts.Length >= 2 ? parts[1].Trim() : string.Empty;
            var shortName = fullName.Split('.').LastOrDefault() ?? fullName;

            return string.IsNullOrEmpty(assembly) ? shortName : $"{shortName} ({assembly})";
        }

        private static void Collect(string itemJson, PendingItem entry)
        {
            if (JToken.Parse(itemJson) is not JContainer root) return;

            foreach (var property in root.DescendantsAndSelf().OfType<JProperty>())
            {
                if (property.Value.Type != JTokenType.String) continue;

                var value = (string?)property.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (FontPropertyNames.Contains(property.Name))
                {
                    entry.Fonts.Add(value);
                }
                else if (property.Name == "$type" && IsPluginType(value))
                {
                    entry.Types.Add(value);
                }
            }
        }

        private static bool IsPluginType(string typeName) =>
            !typeName.Contains('[') && typeName.Contains(", ");

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
