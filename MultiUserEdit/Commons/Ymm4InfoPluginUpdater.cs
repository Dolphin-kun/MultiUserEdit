using System.Net.Http;
using System.Text.Json;
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://ymm4-info.net/api/plugin/{postId}/version");
            request.Headers.Add("x-ymm4-plugin-check", "true");

            using var response = await HttpClientFactory.Client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return [];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            if (!root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("version", out var versionText)
                || !PluginVersion.TryParse(versionText.GetString()?.Trim().TrimStart('v', 'V') ?? string.Empty, out var version))
            {
                return [];
            }

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
