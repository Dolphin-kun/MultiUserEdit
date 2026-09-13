using MultiUserEdit.Commons.EventHandlers;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.Networking;
using MultiUserEdit.ViewModels;
using MultiUserEdit.Views;
using Newtonsoft.Json;
using System.Diagnostics;
using System.IO;
using System.Windows;
using YukkuriMovieMaker.Project;

namespace MultiUserEdit.Commons
{
    internal class CharacterShareManager(
        SessionClient sessionClient,
        FileTransferManager fileTransferManager,
        Func<Guid> getLocalUserId,
        Func<Guid, string> getUserName,
        Func<MultiUserEditViewModel?> getViewModel,
        Action<Action> executeRemoteAction)
    {
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan FileWaitTimeout = TimeSpan.FromMinutes(2);

        private readonly HashSet<string> requested = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> newNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Action>> waiting = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CharacterSharedEvent> received = new(StringComparer.Ordinal);
        private readonly HashSet<string> deciding = new(StringComparer.Ordinal);

        public static bool IsDecided(string characterName) =>
            !string.IsNullOrEmpty(characterName) && CharacterResolver.TryGetMapping(characterName, out _);

        public void RequestIfNeeded(string characterName, Guid ownerId)
        {
            if (string.IsNullOrEmpty(characterName) || IsDecided(characterName)) return;

            lock (requested)
            {
                if (!requested.Add(characterName)) return;
            }

            var evt = new CharacterRequestEvent(characterName, getLocalUserId())
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = getLocalUserId()
            };
            _ = sessionClient.SendAsync(ownerId.ToString(), evt);

            ScheduleReplyTimeout(characterName);
        }

        private void ScheduleReplyTimeout(string characterName)
        {
            _ = Task.Delay(ReplyTimeout).ContinueWith(_ =>
            {
                lock (received)
                {
                    if (received.ContainsKey(characterName) || deciding.Contains(characterName)) return;
                }
                if (IsDecided(characterName)) return;

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (IsDecided(characterName)) return;
                    Debug.WriteLine($"[MultiUserEdit] Character definition not received: {characterName}");
                    Decide(characterName, characterName);
                });
            }, TaskScheduler.Default);
        }

        public void WhenDecided(string characterName, Action action)
        {
            lock (waiting)
            {
                if (!waiting.TryGetValue(characterName, out var actions))
                {
                    actions = [];
                    waiting[characterName] = actions;
                }
                actions.Add(action);
            }
        }

        public void Reset()
        {
            lock (requested) requested.Clear();
            lock (waiting) waiting.Clear();
            lock (received) received.Clear();
            CharacterResolver.ClearMappings();
        }

        public void HandleRequest(CharacterRequestEvent evt)
        {
            var character = CharacterResolver.GetLocalCharacters().FirstOrDefault(c => c.Name == evt.CharacterName);

            string characterJson = string.Empty;
            IReadOnlyList<SharedFile> files = [];

            if (character != null)
            {
                try
                {
                    (characterJson, files) = MediaFileResolver.SerializeCharacterForSync(character);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MultiUserEdit] Failed to serialize character '{evt.CharacterName}': {ex}");
                    characterJson = string.Empty;
                }
            }

            if (evt.IncludeFiles)
            {
                foreach (var file in files)
                {
                    _ = fileTransferManager.SendDirectAsync(file, evt.RequesterId, sessionClient, getLocalUserId());
                }
                return;
            }

            var sharedEvt = new CharacterSharedEvent(evt.CharacterName, characterJson, [.. files.Select(file => file.Name)])
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = getLocalUserId()
            };
            _ = sessionClient.SendAsync(evt.RequesterId.ToString(), sharedEvt);
        }

        public void HandleShared(CharacterSharedEvent evt)
        {
            if (IsDecided(evt.CharacterName)) return;

            lock (received)
            {
                received[evt.CharacterName] = evt;
                if (!deciding.Add(evt.CharacterName)) return;
            }

            var ownerName = getUserName(evt.ExecutorId);
            var characters = CharacterResolver.GetLocalCharacters();

            var canCreate = !string.IsNullOrEmpty(evt.CharacterJson);

            var dialog = new CharacterImportDialog(evt.CharacterName, ownerName, characters, canCreate)
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() == true && dialog.LinkedCharacter == null && canCreate)
            {
                if (TryAskNewName(evt.CharacterName, characters, out var newName))
                {
                    newNames[evt.CharacterName] = newName;
                    CreateCharacter(evt);
                    return;
                }
            }

            var linked = dialog.LinkedCharacter ?? characters.FirstOrDefault(c => c.Name == evt.CharacterName);
            Decide(evt.CharacterName, linked?.Name ?? evt.CharacterName);
        }

        private static bool TryAskNewName(string suggestedName, IReadOnlyList<Character> characters, out string newName)
        {
            var existingNames = characters.Select(c => c.Name).ToList();

            var dialog = new CharacterNameDialog(GetUniqueName(suggestedName, existingNames), existingNames)
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() == true && dialog.CharacterName.Length > 0)
            {
                newName = dialog.CharacterName;
                return true;
            }

            newName = string.Empty;
            return false;
        }

        private void CreateCharacter(CharacterSharedEvent evt)
        {
            var missingFiles = MediaFileResolver.GetMissingFileNames(evt.MediaFileNames);
            if (missingFiles.Count == 0)
            {
                CreateCharacterCore(evt);
                return;
            }

            var request = new CharacterRequestEvent(evt.CharacterName, getLocalUserId(), IncludeFiles: true)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = getLocalUserId()
            };
            _ = sessionClient.SendAsync(evt.ExecutorId.ToString(), request);

            var viewModel = getViewModel();
            if (viewModel == null)
            {
                CreateCharacterCore(evt);
                return;
            }

            TransferWaiter.WhenFilesReady(viewModel, missingFiles, () => CreateCharacterCore(evt), FileWaitTimeout);
        }

        private void CreateCharacterCore(CharacterSharedEvent evt)
        {
            string? failure = null;

            try
            {
                var sharedRoot = TachieFileResolver.GetSharedRoot(evt.CharacterName);
                Directory.CreateDirectory(sharedRoot);

                var characterJson = MediaFileResolver.ResolveCharacterJson(evt.CharacterJson, evt.MediaFileNames, sharedRoot);
                var character = JsonConvert.DeserializeObject<Character>(characterJson, ItemSerializerOptions.Character);

                var characters = CharacterSettingsAccessor.GetCharacters();

                if (character == null)
                {
                    failure = "キャラクターの設定を読み取れませんでした。";
                }
                else if (characters == null)
                {
                    failure = $"YMM4のキャラクター一覧を取得できませんでした。({CharacterSettingsAccessor.UnavailableReason})";
                }
                else
                {
                    newNames.Remove(evt.CharacterName, out var requestedName);
                    character.Name = GetUniqueName(
                        string.IsNullOrWhiteSpace(requestedName) ? evt.CharacterName : requestedName,
                        characters.Select(c => c.Name));

                    characters.Add(character);
                    CharacterSettingsAccessor.Save();

                    Decide(evt.CharacterName, character.Name);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Failed to create character: {ex}");
                failure = ex.Message;
            }

            MessageBox.Show(
                $"キャラクター「{evt.CharacterName}」を追加できませんでした。\n{failure}\n\n" +
                "同名のキャラクターがある場合はそちらに紐づけて続行します。",
                "キャラクターの読み込み",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Decide(evt.CharacterName, evt.CharacterName);
        }

        private static string GetUniqueName(string baseName, IEnumerable<string> existingNames)
        {
            var names = existingNames.ToHashSet(StringComparer.Ordinal);
            if (!names.Contains(baseName)) return baseName;

            for (var index = 2; ; index++)
            {
                var candidate = $"{baseName} ({index})";
                if (!names.Contains(candidate)) return candidate;
            }
        }

        private void Decide(string remoteName, string localName)
        {
            CharacterResolver.SetMapping(remoteName, localName);

            List<Action>? actions;
            lock (waiting)
            {
                waiting.Remove(remoteName, out actions);
            }
            lock (received)
            {
                received.Remove(remoteName);
                deciding.Remove(remoteName);
            }

            if (actions == null) return;

            foreach (var action in actions)
            {
                executeRemoteAction(action);
            }
        }
    }
}
