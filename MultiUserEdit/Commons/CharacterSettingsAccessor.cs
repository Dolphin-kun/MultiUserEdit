using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using YukkuriMovieMaker.Project;

namespace MultiUserEdit.Commons
{
    internal static class CharacterSettingsAccessor
    {
        private static object? settingsInstance;
        private static PropertyInfo? charactersProperty;
        private static MethodInfo? saveMethod;
        private static bool initialized;

        public static string? UnavailableReason { get; private set; }

        public static ObservableCollection<Character>? GetCharacters()
        {
            EnsureInitialized();

            try
            {
                if (charactersProperty?.GetValue(settingsInstance) is ObservableCollection<Character> characters)
                {
                    return characters;
                }

                UnavailableReason ??= "キャラクター一覧を読み取れませんでした。";
                return null;
            }
            catch (Exception ex)
            {
                UnavailableReason = ex.Message;
                Debug.WriteLine($"[MultiUserEdit] Failed to read characters: {ex}");
                return null;
            }
        }

        public static void Save()
        {
            EnsureInitialized();

            try
            {
                saveMethod?.Invoke(settingsInstance, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Failed to save character settings: {ex.Message}");
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;

            try
            {
                var settingsType = typeof(Character).Assembly.GetType("YukkuriMovieMaker.Settings.CharacterSettings");
                if (settingsType == null)
                {
                    UnavailableReason = "CharacterSettings 型が見つかりません。";
                    return;
                }

                settingsInstance = FindDefaultInstance(settingsType);
                if (settingsInstance == null)
                {
                    UnavailableReason = "CharacterSettings のインスタンスを取得できません。";
                    return;
                }

                charactersProperty = settingsType.GetProperty("Characters", BindingFlags.Public | BindingFlags.Instance);
                if (charactersProperty == null)
                {
                    UnavailableReason = "CharacterSettings.Characters が見つかりません。";
                    return;
                }

                saveMethod = settingsType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            }
            catch (Exception ex)
            {
                UnavailableReason = ex.Message;
                Debug.WriteLine($"[MultiUserEdit] CharacterSettings not available: {ex}");
            }
        }

        private static object? FindDefaultInstance(Type settingsType)
        {
            for (var type = settingsType; type != null; type = type.BaseType)
            {
                var instance = type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                            ?? type.GetField("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

                if (instance != null && settingsType.IsInstanceOfType(instance)) return instance;
            }

            return null;
        }
    }
}
