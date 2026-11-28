using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Settings;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        private bool applyingRemoteScript;
        private AsyncAwaitStatus? addItemStatus;
        private static readonly TimeSpan ScriptStartWait = TimeSpan.FromSeconds(3);
        private bool addingScripts;
        private readonly Dictionary<Guid, System.Windows.Threading.DispatcherTimer> scriptUpdateTimers = [];
        private static readonly TimeSpan ScriptUpdateDelay = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan ScriptCommandWait = TimeSpan.FromMinutes(5);

        public ObservableCollection<YukkuriMovieMaker.Project.Character> ScriptCharacters => CharacterSettingsAccessor.GetCharacters() ?? [];

        private void OnScriptsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            foreach (var entry in e.OldItems?.OfType<ScriptEntry>() ?? []) entry.PropertyChanged -= OnScriptEntryChanged;
            foreach (var entry in e.NewItems?.OfType<ScriptEntry>() ?? []) entry.PropertyChanged += OnScriptEntryChanged;
        }

        private void OnScriptEntryChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ScriptEntry entry || e.PropertyName is not (nameof(ScriptEntry.CharacterName) or nameof(ScriptEntry.Serif))) return;

            if (!entry.IsShared)
            {
                if (ReferenceEquals(Scripts.LastOrDefault(), entry) && !string.IsNullOrEmpty(entry.Serif)) Scripts.Add(new ScriptEntry(entry.CharacterName));
                return;
            }

            if (applyingRemoteScript) return;
            if (!scriptUpdateTimers.TryGetValue(entry.ScriptId, out var timer))
            {
                timer = new System.Windows.Threading.DispatcherTimer { Interval = ScriptUpdateDelay };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    scriptUpdateTimers.Remove(entry.ScriptId);
                    if (Scripts.Contains(entry) && IsValidScriptText(entry.CharacterName, entry.Serif)) BroadcastScript("update", entry.ToItem());
                };
                scriptUpdateTimers[entry.ScriptId] = timer;
            }
            timer.Stop();
            timer.Start();
        }

        private static bool IsValidScriptText(string? characterName, string? serif) =>
            !string.IsNullOrWhiteSpace(serif)
            && serif.Length <= MaxSerifLength
            && !string.IsNullOrWhiteSpace(characterName)
            && characterName.Length <= MaxNameLength;

        private string DefaultScriptCharacter() =>
            Scripts.LastOrDefault()?.CharacterName is { Length: > 0 } last ? last
            : ScriptCharacters.FirstOrDefault()?.Name ?? string.Empty;

        private void EnsureTrailingScriptRow()
        {
            var last = Scripts.LastOrDefault();
            if (last == null || last.IsShared || !string.IsNullOrEmpty(last.Serif)) Scripts.Add(new ScriptEntry(DefaultScriptCharacter()));
        }

        private void ClearSharedScripts()
        {
            foreach (var entry in Scripts.Where(s => s.IsShared).ToList()) Scripts.Remove(entry);
            EnsureTrailingScriptRow();
        }

        private void InsertSharedScript(ScriptEntry entry)
        {
            var firstLocal = Scripts.Select((s, i) => (s, i)).FirstOrDefault(x => !x.s.IsShared, (null!, Scripts.Count)).i;
            Scripts.Insert(firstLocal, entry);
        }

        private void HandleScript(ScriptEvent script)
        {
            var item = script.Script;
            if (item == null) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            dispatcher.InvokeAsync(() =>
            {
                var existing = Scripts.FirstOrDefault(s => s.IsShared && s.ScriptId == item.ScriptId);
                var fromYmm4 = !webParticipants.ContainsKey(script.ExecutorId) && Participants.Any(p => p.UserId == script.ExecutorId);
                switch (script.Action)
                {
                    case "add":
                        if (item.AuthorId != script.ExecutorId) return;
                        if (!IsValidScriptText(item.CharacterName, item.Serif)) return;
                        if ((item.AuthorName ?? string.Empty).Length > MaxNameLength) return;
                        if (existing != null || Scripts.Count(s => s.IsShared) >= MaxScripts) return;
                        InsertSharedScript(new ScriptEntry(item));
                        break;
                    case "update":
                        if (existing == null || !(existing.AuthorId == script.ExecutorId || fromYmm4)) return;
                        if (!IsValidScriptText(item.CharacterName, item.Serif)) return;
                        applyingRemoteScript = true;
                        try
                        {
                            existing.CharacterName = item.CharacterName;
                            existing.Serif = item.Serif;
                        }
                        finally
                        {
                            applyingRemoteScript = false;
                        }
                        break;
                    case "remove":
                    case "generated":
                        if (existing == null || !(existing.AuthorId == script.ExecutorId || fromYmm4)) return;
                        Scripts.Remove(existing);
                        EnsureTrailingScriptRow();
                        break;
                }
            });
        }

        private void SendScriptList(Guid target)
        {
            if (!IsHost) return;
            var shared = Scripts.Where(s => s.IsShared).Select(s => s.ToItem()).ToList();
            if (shared.Count == 0) return;
            _ = sessionClient.SendAsync(target.ToString(), new ScriptListEvent(shared) { DateTime = DateTime.UtcNow, ExecutorId = LocalUserId });
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > until) return false;
                await Task.Delay(100);
            }
            return true;
        }

        private async Task AddScriptsToTimelineAsync()
        {
            if (addingScripts) return;
            addingScripts = true;
            try
            {
                var command = YukkuriMovieMaker.Plugin.SettingsBase<CommandSettings>.Default[CommandType.AddVoiceItem];
                var target = MainView;
                foreach (var entry in Scripts.Where(s => !string.IsNullOrWhiteSpace(s.Serif)).ToList())
                {
                    if (!Scripts.Contains(entry)) continue;

                    var character = ScriptCharacters.FirstOrDefault(c => c.Name == entry.CharacterName);
                    if (character == null)
                    {
                        MessageBox.Show(string.Format(Texts.ScriptCharacterMissing, entry.CharacterName), Texts.Scripts, MessageBoxButton.OK);
                        return;
                    }

                    var parameter = new YukkuriMovieMaker.ViewModels.CommandParameter.AddVoiceItemCommandParameter(0, 0, character, entry.Serif.Trim(), []);
                    if (command == null || target == null || !await WaitUntilAsync(() => command.CanExecute(parameter, target), ScriptCommandWait))
                    {
                        MessageBox.Show(Texts.ScriptGenerateFailed, Texts.Scripts, MessageBoxButton.OK);
                        return;
                    }

                    command.Execute(parameter, target);
                    Scripts.Remove(entry);
                    if (entry.IsShared) BroadcastScript("generated", entry.ToItem());
                    if (addItemStatus is { } status)
                    {
                        await WaitUntilAsync(() => status.IsBusy, ScriptStartWait);
                        await WaitUntilAsync(() => !status.IsBusy, ScriptCommandWait);
                    }
                }
            }
            finally
            {
                addingScripts = false;
                EnsureTrailingScriptRow();
            }
        }

        private void DeleteScript(ScriptEntry entry)
        {
            Scripts.Remove(entry);
            if (entry.IsShared) BroadcastScript("remove", entry.ToItem());
            EnsureTrailingScriptRow();
        }

        private void BroadcastScript(string action, ScriptItem item)
        {
            if (!IsConnected) return;
            _ = sessionClient.SendAsync(null, new ScriptEvent(action, item) { DateTime = DateTime.UtcNow, ExecutorId = LocalUserId });
        }
    }
}
