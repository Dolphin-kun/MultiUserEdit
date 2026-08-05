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

        public static Character? FindCharacter(string name, IEnumerable<Character>? characters = null)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (characters != null)
            {
                var found = characters.FirstOrDefault(c => c.Name == name);
                if (found != null) return found;
            }

            var uiCharacters = GetCharactersFromActiveTimelineView();
            if (uiCharacters != null)
            {
                var found = uiCharacters.FirstOrDefault(c => c.Name == name);
                if (found != null) return found;
            }

            return null;
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
