using Newtonsoft.Json.Linq;
using System.Windows;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;

namespace MultiUserEdit.Commons
{
    internal static class ResourceAvailabilityChecker
    {
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);
        public static string MissingHereTitle => Texts.MissingHereTitle;
        public static string MissingThereTitle => Texts.MissingThereTitle;

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
                reportedFontOwners.Clear();
                reportedPluginOwners.Clear();
                alreadyToldAboutFont.Clear();
                alreadyToldAboutPlugin.Clear();
                scheduleGeneration++;
                reportGeneration++;
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
            catch
            {
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

            var who = senders.Count == 1 ? senders.First() : Texts.Collaborator;

            foreach (var plugin in newPlugins)
            {
                ErrorNotifier.NotifyList(MissingHereTitle,
                    string.Format(Texts.PeerPluginMissingLead, who),
                    ErrorNotifier.Quote(plugin),
                    Texts.PluginMissingNote,
                    once: false);
            }

            foreach (var font in newFonts)
            {
                ErrorNotifier.NotifyList(MissingHereTitle,
                    string.Format(Texts.PeerFontMissingLead, who),
                    ErrorNotifier.Quote(font),
                    Texts.FontMissingNote,
                    once: false);
            }
        }

        private static readonly Dictionary<string, SortedSet<string>> reportedFontOwners = [];
        private static readonly Dictionary<string, SortedSet<string>> reportedPluginOwners = [];
        private static readonly HashSet<string> alreadyToldAboutFont = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> alreadyToldAboutPlugin = new(StringComparer.OrdinalIgnoreCase);
        private static int reportGeneration;

        public static void NotifyReportedByPeer(string peerName, string[] fonts, string[] plugins)
        {
            if (fonts.Length == 0 && plugins.Length == 0) return;

            lock (gate)
            {
                Collect(reportedFontOwners, fonts, peerName, alreadyToldAboutFont);
                Collect(reportedPluginOwners, plugins, peerName, alreadyToldAboutPlugin);

                if (reportedFontOwners.Count == 0 && reportedPluginOwners.Count == 0) return;

                var generation = ++reportGeneration;
                _ = Task.Delay(QuietPeriod).ContinueWith(
                    _ => Application.Current?.Dispatcher.InvokeAsync(() => FlushReports(generation)),
                    TaskScheduler.Default);
            }
        }

        private static void Collect(Dictionary<string, SortedSet<string>> target, string[] names, string peerName, HashSet<string> alreadyTold)
        {
            foreach (var name in names)
            {
                if (alreadyTold.Contains(name)) continue;

                if (!target.TryGetValue(name, out var owners))
                {
                    owners = new SortedSet<string>(StringComparer.Ordinal);
                    target[name] = owners;
                }

                if (!string.IsNullOrWhiteSpace(peerName)) owners.Add(peerName);
            }
        }

        private static void FlushReports(int generation)
        {
            List<string> fontLines;
            List<string> pluginLines;

            lock (gate)
            {
                if (generation != reportGeneration) return;

                fontLines = BuildLines(reportedFontOwners, alreadyToldAboutFont);
                pluginLines = BuildLines(reportedPluginOwners, alreadyToldAboutPlugin);
                reportedFontOwners.Clear();
                reportedPluginOwners.Clear();
            }

            var note = Texts.PeerScreenNote;

            foreach (var line in pluginLines)
                ErrorNotifier.NotifyList(MissingThereTitle, Texts.MyPluginMissingLead, line, note, once: false);

            foreach (var line in fontLines)
                ErrorNotifier.NotifyList(MissingThereTitle, Texts.MyFontMissingLead, line, note, once: false);
        }

        private static List<string> BuildLines(Dictionary<string, SortedSet<string>> source, HashSet<string> alreadyTold)
        {
            var lines = new List<string>();

            foreach (var (name, owners) in source.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!alreadyTold.Add(name)) continue;

                var who = owners.Count switch
                {
                    0 => string.Empty,
                    <= 3 => string.Format(Texts.OwnersFew, string.Join(Texts.ListSeparator, owners)),
                    _ => string.Format(Texts.OwnersMany, owners.First(), owners.Count - 1)
                };

                lines.Add(ErrorNotifier.Quote(name) + who);
            }

            return lines;
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
            catch
            {
            }

            return names;
        }
    }
}
