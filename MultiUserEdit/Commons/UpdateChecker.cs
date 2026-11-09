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
        public const string PostId = "1789275383618793";

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
                _ => OpenUpdatePage());
        }

        public void EnsureChecked()
        {
            checkTask ??= CheckAsync();
        }

        public async Task<bool> IsOutdatedAsync(TimeSpan maxWait)
        {
            EnsureChecked();
            await Task.WhenAny(checkTask!, Task.Delay(maxWait));
            return HasUpdate;
        }

        public void OpenUpdatePage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = string.IsNullOrEmpty(downloadUrl) ? FallbackUrl : downloadUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static bool IsNewer(string? candidate, string? current)
            => TryParse(candidate, out var next) && TryParse(current, out var now) && Compare(next, now) > 0;

        private static bool TryParse(string? text, out (Version Core, string[] PreRelease) parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var trimmed = text.Trim().TrimStart('v', 'V');
            var plus = trimmed.IndexOf('+');
            if (plus >= 0) trimmed = trimmed[..plus];

            var dash = trimmed.IndexOf('-');
            var core = dash >= 0 ? trimmed[..dash] : trimmed;

            if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out var version)) return false;

            parsed = (Normalize(version), dash >= 0 ? trimmed[(dash + 1)..].Split('.') : []);
            return true;
        }

        private static Version Normalize(Version version)
            => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

        private static int Compare((Version Core, string[] PreRelease) left, (Version Core, string[] PreRelease) right)
        {
            var core = left.Core.CompareTo(right.Core);
            if (core != 0) return core;

            if (left.PreRelease.Length == 0 || right.PreRelease.Length == 0)
                return (left.PreRelease.Length == 0).CompareTo(right.PreRelease.Length == 0);

            for (var i = 0; i < Math.Min(left.PreRelease.Length, right.PreRelease.Length); i++)
            {
                var a = left.PreRelease[i];
                var b = right.PreRelease[i];

                var aIsNumber = int.TryParse(a, out var aNumber);
                var bIsNumber = int.TryParse(b, out var bNumber);

                var order = (aIsNumber, bIsNumber) switch
                {
                    (true, true) => aNumber.CompareTo(bNumber),
                    (true, false) => -1,
                    (false, true) => 1,
                    _ => string.CompareOrdinal(a, b),
                };

                if (order != 0) return order;
            }

            return left.PreRelease.Length.CompareTo(right.PreRelease.Length);
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

                HasUpdate = IsNewer(latest, CurrentVersion);
            }
            catch
            {
                LatestVersion = "取得できませんでした";
            }
        }
    }
}
