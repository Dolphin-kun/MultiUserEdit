using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;

namespace MultiUserEdit.Commons
{
    internal static class AudioProxy
    {
        private const int Bitrate = 128000;
        private const int TargetHz = 48000;
        private const long PassThroughLimit = 30L * 1024 * 1024;

        private static readonly HashSet<string> CompressedAudio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".ogg", ".opus" };
        private static readonly ConcurrentDictionary<string, Task<string?>> running = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim gate = new(1, 1);

        private static string CacheDirectory => Path.Combine(YukkuriMovieMaker.Commons.AppDirectories.TemporaryDirectory, "MultiUserEdit", "WebAudio");

        public static void DeleteCache()
        {
            try
            {
                if (!Directory.Exists(CacheDirectory)) return;
                foreach (var file in Directory.GetFiles(CacheDirectory))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch
            {
            }
        }

        public static bool CanSendAsIs(string path)
        {
            try
            {
                return CompressedAudio.Contains(Path.GetExtension(path)) && new FileInfo(path).Length <= PassThroughLimit;
            }
            catch
            {
                return false;
            }
        }

        public static string? CachedPath(string path)
        {
            var target = TargetPath(path);
            return target != null && File.Exists(target) ? target : null;
        }

        public static Task<string?> GetOrCreateAsync(string path)
        {
            var target = TargetPath(path);
            if (target == null) return Task.FromResult<string?>(null);
            if (File.Exists(target)) return Task.FromResult<string?>(target);

            return running.GetOrAdd(target, _ => Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    return File.Exists(target) ? target : Encode(path, target);
                }
                finally
                {
                    gate.Release();
                    running.TryRemove(target, out Task<string?>? _);
                }
            }));
        }

        private static string? TargetPath(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
                return Path.Combine(CacheDirectory, hash + ".m4a");
            }
            catch
            {
                return null;
            }
        }

        private static string? Encode(string path, string target)
        {
            var temp = target + ".tmp.m4a";
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                using var source = AudioFileSourceFactory.Create(path, 0);
                if (source == null || source.Duration <= TimeSpan.Zero) return null;

                MediaFoundationApi.Startup();
                ISampleProvider samples = new FileSourceSampleProvider(source);
                if (samples.WaveFormat.SampleRate != TargetHz && samples.WaveFormat.SampleRate != 44100)
                    samples = new WdlResamplingSampleProvider(samples, TargetHz);

                MediaFoundationEncoder.EncodeToAac(new SampleToWaveProvider16(samples), temp, Bitrate);
                File.Move(temp, target, overwrite: true);
                return target;
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                return null;
            }
        }

        private sealed class FileSourceSampleProvider(IAudioFileSource source) : ISampleProvider
        {
            private readonly long totalSamples = (long)Math.Ceiling(source.Duration.TotalSeconds * source.Hz) * 2;
            private long position;

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.Hz, 2);

            private float[] scratch = [];

            public int Read(Span<float> buffer)
            {
                var remaining = totalSamples - position;
                if (remaining <= 0) return 0;
                var wanted = (int)Math.Min(buffer.Length, remaining);
                if (scratch.Length < wanted) scratch = new float[wanted];
                var read = source.Read(scratch, 0, wanted);
                if (read <= 0) return 0;
                scratch.AsSpan(0, read).CopyTo(buffer);
                position += read;
                return read;
            }
        }
    }
}
