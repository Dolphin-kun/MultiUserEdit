using System.Diagnostics;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class VoiceSynthesizer
    {
        private static readonly SemaphoreSlim queue = new(1, 1);

        private static readonly HashSet<object> synthesizing = new(ReferenceEqualityComparer.Instance);

        public static void Request(IItem? item)
        {
            if (item is not VoiceItem voice) return;
            if (string.IsNullOrWhiteSpace(voice.Serif)) return;

            _ = GenerateAsync(voice);
        }

        public static string? GetSerif(IItem? item) => (item as VoiceItem)?.Serif;

        public static void RequestIfSerifChanged(IItem? item, string? previousSerif)
        {
            if (item is not VoiceItem voice) return;
            if (string.Equals(voice.Serif, previousSerif, StringComparison.Ordinal)) return;

            Request(item);
        }

        public static bool IsSynthesizing(IItem? item)
        {
            if (item is not VoiceItem) return false;

            lock (synthesizing) return synthesizing.Contains(item);
        }

        private static async Task GenerateAsync(VoiceItem voice)
        {
            await queue.WaitAsync();
            try
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Could not synthesize the voice locally: {ex.Message}");
            }
            finally
            {
                queue.Release();
            }
        }
    }
}
