using Reactive.Bindings;
using System.Windows;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.ViewModels;
using YukkuriMovieMaker.Views;

namespace MultiUserEdit.Commons
{
    internal static class CharacterResolver
    {
        private static readonly Dictionary<string, string> nameMapping = new(StringComparer.Ordinal);

        public static void SetMapping(string remoteName, string localName)
        {
            lock (nameMapping) nameMapping[remoteName] = localName;
        }

        public static bool TryGetMapping(string remoteName, out string localName)
        {
            lock (nameMapping) return nameMapping.TryGetValue(remoteName, out localName!);
        }

        public static void ClearMappings()
        {
            lock (nameMapping) nameMapping.Clear();
        }

        private static string MapName(string name) =>
            TryGetMapping(name, out var localName) ? localName : name;

        public static void TryResolveCharacter(IItem item, IEnumerable<Character>? characters = null)
        {
            string? targetName = null;
            Action<Character>? setCharacterAction = null;

            if (item is VoiceItem voiceItem)
            {
                targetName = voiceItem.CharacterName;
                setCharacterAction = c => voiceItem.Character = c;
            }
            else if (item is TachieItem tachieItem)
            {
                targetName = tachieItem.CharacterName;
                setCharacterAction = c => tachieItem.Character = c;
            }
            else if (item is TachieFaceItem tachieFaceItem)
            {
                targetName = tachieFaceItem.CharacterName;
                setCharacterAction = c => tachieFaceItem.Character = c;
            }

            if (string.IsNullOrEmpty(targetName) || setCharacterAction == null) return;

            var character = FindCharacter(targetName, characters);
            if (character != null)
            {
                setCharacterAction(character);
            }
        }

        public static Character? GetCharacter(IItem item) => item switch
        {
            VoiceItem voiceItem => voiceItem.Character,
            TachieItem tachieItem => tachieItem.Character,
            TachieFaceItem tachieFaceItem => tachieFaceItem.Character,
            _ => null
        };

        public static Character? FindCharacter(string name, IEnumerable<Character>? characters = null)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var localName = MapName(name);

            if (characters != null)
            {
                var found = characters.FirstOrDefault(c => c.Name == localName);
                if (found != null) return found;
            }

            return GetLocalCharacters().FirstOrDefault(c => c.Name == localName);
        }

        public static IReadOnlyList<Character> GetLocalCharacters()
        {
            var settingsCharacters = CharacterSettingsAccessor.GetCharacters();
            if (settingsCharacters != null) return [.. settingsCharacters];

            var uiCharacters = GetCharactersFromActiveTimelineView();
            return uiCharacters == null ? [] : [.. uiCharacters];
        }

        private static ReadOnlyReactiveCollection<Character>? GetCharactersFromActiveTimelineView()
        {
            try
            {
                return Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (Window window in Application.Current.Windows)
                    {
                        var timelineView = VisualTreeHelperExtensions.FindVisualChild<TimelineView>(window);
                        if (timelineView?.DataContext is TimelineViewModel timelineViewModel)
                        {
                            return timelineViewModel.Characters;
                        }
                    }
                    return null;
                });
            }
            catch
            {
                return null;
            }
        }
    }
}
