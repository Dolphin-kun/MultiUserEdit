using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons
{
    internal sealed class UpdateChecker : Bindable
    {
        private const string PostId = "";

        private const string FallbackUrl = "https://ymm4-info.net/";

        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        public static UpdateChecker Instance { get; } = new();

        private Task? checkTask;

        public string CurrentVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        private string latestVersion = "確認中...";
        public string LatestVersion
        {
            get => latestVersion;
            private set => Set(ref latestVersion, value);
        }

        private bool hasUpdate;
        public bool HasUpdate
        {
            get => hasUpdate;
            private set => Set(ref hasUpdate, value);
        }

        private string downloadUrl = string.Empty;

        public ICommand OpenUpdateUrlCommand { get; }

        private UpdateChecker()
        {
            OpenUpdateUrlCommand = new ActionCommand(
                _ => HasUpdate,
                _ => Process.Start(new ProcessStartInfo
                {
                    FileName = string.IsNullOrEmpty(downloadUrl) ? FallbackUrl : downloadUrl,
                    UseShellExecute = true
                }));
        }

        public void EnsureChecked()
        {
            checkTask ??= CheckAsync();
        }

        private async Task CheckAsync()
        {
            if (string.IsNullOrEmpty(PostId))
            {
                LatestVersion = "未公開";
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"https://ymm4-info.net/api/plugin/{PostId}/version");
                request.Headers.Add("x-ymm4-plugin-check", "true");

                var response = await httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    LatestVersion = "取得できませんでした";
                    return;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                if (!root.TryGetProperty("success", out var success) || !success.GetBoolean() ||
                    !root.TryGetProperty("version", out var versionElement))
                {
                    LatestVersion = "取得できませんでした";
                    return;
                }

                var latest = versionElement.GetString();
                LatestVersion = string.IsNullOrWhiteSpace(latest) ? "不明" : latest;

                if (root.TryGetProperty("downloadURL", out var urlElement))
                    downloadUrl = urlElement.GetString() ?? string.Empty;

                if (Version.TryParse(latest?.TrimStart('v', 'V'), out var latestParsed) &&
                    Version.TryParse(CurrentVersion, out var currentParsed))
                {
                    HasUpdate = latestParsed > currentParsed;
                }
            }
            catch
            {
                LatestVersion = "取得できませんでした";
            }
        }
    }
}
