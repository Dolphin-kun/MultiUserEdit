using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace MultiUserEdit.Commons
{
    internal static class RelayAvailabilityChecker
    {
        private const string StatusUrl = "https://mue-relay.ymm4-info.net/status";

        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<(bool Reachable, string Detail)> CheckAsync()
        {
            try
            {
                using var response = await httpClient.GetAsync(StatusUrl);

                return response.IsSuccessStatusCode
                    ? (true, string.Empty)
                    : (false, string.Format(Texts.NetHttpStatus, (int)response.StatusCode));
            }
            catch (Exception ex)
            {
                return (false, Describe(ex));
            }
        }

        public static string Describe(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                switch (current)
                {
                    case SocketException socketError:
                        return DescribeSocketError(socketError.SocketErrorCode);

                    case AuthenticationException:
                        return Texts.NetTls;

                    case WebSocketException webSocketError:
                        return string.Format(Texts.NetWebSocket, webSocketError.WebSocketErrorCode);

                    case TaskCanceledException:
                    case TimeoutException:
                        return Texts.NetTimeout;
                }
            }

            return $"{exception.GetType().Name}: {exception.Message}";
        }

        private static string DescribeSocketError(SocketError error) => error switch
        {
            SocketError.HostNotFound or SocketError.NoData =>
                Texts.NetDns,
            SocketError.TimedOut =>
                Texts.NetNoResponse,
            SocketError.ConnectionRefused =>
                Texts.NetRefused,
            SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                Texts.NetUnreachable,
            SocketError.AccessDenied =>
                Texts.NetBlocked,
            _ => string.Format(Texts.NetFailed, error)
        };
    }
}
