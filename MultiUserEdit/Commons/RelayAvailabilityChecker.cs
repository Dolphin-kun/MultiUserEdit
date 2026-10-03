using System.Net.Http;

namespace MultiUserEdit.Commons
{
    internal static class RelayAvailabilityChecker
    {
        private const string StatusUrl = "https://mue-relay.ymm4-info.net/status";

        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        public static async Task<bool> IsAvailableAsync()
        {
            try
            {
                using var response = await httpClient.GetAsync(StatusUrl);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
