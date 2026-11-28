using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Update;

namespace MultiUserEdit.Commons
{
    public sealed class Ymm4InfoPluginUpdater<T>(string postId) : IPluginUpdater where T : IPlugin
    {
        public string PluginUrl => $"https://ymm4-info.net/plugin/{postId}";

        public PluginVersion CurrentVersion => PluginVersion.FromAssemblyInformationalVersion(typeof(T));

        public async Task<IPluginUpdateInfo[]> GetUpdatesAsync()
        {
            var published = await UpdateChecker.Instance.GetPublishedVersionAsync();
            if (published == null || !PluginVersion.TryParse(published, out var version))
                return [];

            return [new Ymm4InfoPluginUpdateInfo(version)];
        }
    }

    file sealed class Ymm4InfoPluginUpdateInfo(PluginVersion version) : IPluginUpdateInfo
    {
        public string DisplayName => version.Version.ToString();
        public PluginVersion Version => version;
        public DescriptionFormat DescriptionFormat => DescriptionFormat.PlainText;
        public string Description => string.Empty;
        public bool IsPrerelease => false;
        public bool CanDownload => false;

        public Task<string> DownloadAsync(string dir, ProgressMessage progress, CancellationToken token)
            => throw new NotSupportedException();
    }
}
