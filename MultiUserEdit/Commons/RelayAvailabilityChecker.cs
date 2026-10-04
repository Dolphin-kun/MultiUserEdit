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
                    : (false, $"サーバーが HTTP {(int)response.StatusCode} を返しました。");
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
                        return "暗号化通信の確認に失敗しました。セキュリティソフトの通信検査やプロキシが原因の可能性があります。";

                    case WebSocketException webSocketError:
                        return $"接続を確立できませんでした（{webSocketError.WebSocketErrorCode}）。";

                    case TaskCanceledException:
                    case TimeoutException:
                        return "サーバーの応答がありませんでした（タイムアウト）。";
                }
            }

            return $"{exception.GetType().Name}: {exception.Message}";
        }

        private static string DescribeSocketError(SocketError error) => error switch
        {
            SocketError.HostNotFound or SocketError.NoData =>
                "サーバー名を解決できませんでした（DNS）。別の DNS（例: 1.1.1.1）に変えると直ることがあります。",
            SocketError.TimedOut =>
                "サーバーに接続できませんでした（応答なし）。セキュリティソフトやルーターが 443 番の通信を止めている可能性があります。",
            SocketError.ConnectionRefused =>
                "サーバーに接続を拒否されました。",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                "サーバーまでの経路がありません。IPv4 で接続できない回線の可能性があります。",
            SocketError.AccessDenied =>
                "通信がブロックされました。セキュリティソフトの設定を確認してください。",
            _ => $"通信に失敗しました（{error}）。"
        };
    }
}
