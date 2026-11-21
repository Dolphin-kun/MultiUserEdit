using Newtonsoft.Json;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class VoiceSynthesizer
    {
        private static string FailureTitle => Texts.VoiceFailedTitle;

        private static readonly TimeSpan EngineResponseLimit = TimeSpan.FromSeconds(30);

        private static readonly SemaphoreSlim queue = new(1, 1);

        private static readonly HashSet<object> synthesizing = new(ReferenceEqualityComparer.Instance);

        public readonly record struct VoiceState(string? Serif, string? Hatsuon, string? CharacterName, string? Parameter);

        public static void Request(IItem? item)
        {
            if (item is not VoiceItem voice) return;
            if (string.IsNullOrWhiteSpace(voice.Serif)) return;

            _ = GenerateAsync(voice);
        }

        public static VoiceState? Capture(IItem? item) =>
            item is VoiceItem voice
                ? new VoiceState(voice.Serif, voice.Hatsuon, voice.CharacterName, DescribeParameter(voice))
                : null;

        public static void RequestIfVoiceChanged(IItem? item, VoiceState? previous)
        {
            if (previous is not { } before) return;
            if (Capture(item) is not { } after || after == before) return;

            Request(item);
        }

        public static bool IsSynthesizing(IItem? item)
        {
            if (item is not VoiceItem) return false;

            lock (synthesizing) return synthesizing.Contains(item);
        }

        private static string? DescribeParameter(VoiceItem voice)
        {
            try
            {
                return voice.VoiceParameter == null
                    ? null
                    : JsonConvert.SerializeObject(voice.VoiceParameter, ItemSerializerOptions.Default);
            }
            catch
            {
                return null;
            }
        }

        private static void Report(string lead, string item, string note)
        {
            ErrorNotifier.NotifyList(FailureTitle, lead, item, note);

            try
            {
                YukkuriMovieMaker.Commons.Log.Default.Write($"[MultiUserEdit] {lead} {item}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] could not write to the YMM4 log: {ex.Message}");
            }
        }

        private static bool CanSynthesize(VoiceItem voice)
        {
            var name = voice.CharacterName ?? string.Empty;
            var character = voice.Character;

            if (character == null)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    Report(
                        Texts.VoiceCharacterMissingLead,
                        ErrorNotifier.Quote(name),
                        Texts.VoiceCharacterMissingNote);
                }
                return false;
            }

            var speaker = character.Voice?.Speaker;
            if (speaker == null)
            {
                Report(
                    Texts.VoiceNotSetLead,
                    ErrorNotifier.Quote(name),
                    Texts.VoiceNotSetNote);
                return false;
            }

            if (string.IsNullOrWhiteSpace(voice.Hatsuon)) return false;

            if (speaker.CreateVoiceParameter()?.GetType() != voice.VoiceParameter?.GetType())
            {
                Report(
                    Texts.VoiceEngineMismatchLead,
                    ErrorNotifier.Quote(name),
                    Texts.VoiceEngineMismatchNote);
                return false;
            }

            return true;
        }

        private static void ReportEngineError(VoiceItem voice)
        {
            if (voice is not INotifyDataErrorInfo { HasErrors: true } errors) return;

            foreach (var error in (errors.GetErrors(nameof(VoiceItem.Hatsuon)) as IEnumerable) ?? Array.Empty<object>())
            {
                var message = error?.ToString();
                if (string.IsNullOrWhiteSpace(message)) continue;

                Report(
                    Texts.VoiceEngineErrorLead,
                    $"{ErrorNotifier.Quote(voice.CharacterName ?? string.Empty)}　{message}",
                    Texts.VoiceEngineErrorNote);
            }
        }

        private static async Task SynthesizeAsync(VoiceItem voice)
        {
            lock (synthesizing) synthesizing.Add(voice);
            try
            {
                await voice.CreateVoiceFileAsync();
            }
            finally
            {
                lock (synthesizing) synthesizing.Remove(voice);
            }
        }

        private static async Task GenerateAsync(VoiceItem voice)
        {
            await queue.WaitAsync();

            var released = 0;
            void ReleaseQueue()
            {
                if (Interlocked.Exchange(ref released, 1) == 0) queue.Release();
            }

            try
            {
                if (!CanSynthesize(voice)) return;

                var synthesis = SynthesizeAsync(voice);
                if (await Task.WhenAny(synthesis, Task.Delay(EngineResponseLimit)) != synthesis)
                {
                    Report(
                        Texts.VoiceEngineSlowLead,
                        ErrorNotifier.Quote(voice.CharacterName ?? string.Empty),
                        Texts.VoiceEngineSlowNote);

                    ReleaseQueue();
                    await synthesis;
                }

                ReportEngineError(voice);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Could not synthesize the voice locally: {ex.Message}");
            }
            finally
            {
                ReleaseQueue();
            }
        }
    }
}
