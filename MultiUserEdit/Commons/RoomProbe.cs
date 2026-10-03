using MultiUserEdit.Networking;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MultiUserEdit.Commons
{
    internal static class RoomProbe
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        public static Task<bool> CreateRoomAsync(string roomId, string hostKey) =>
            TryConnectAsync(roomId, "host", hostKey);

        public static Task<bool> CanJoinAsync(string roomId) =>
            TryConnectAsync(roomId, "guest", null);

        private static async Task<bool> TryConnectAsync(string roomId, string role, string? hostKey)
        {
            using var cts = new CancellationTokenSource(Timeout);
            using var socket = new ClientWebSocket();

            socket.Options.SetRequestHeader("X-Client-Version", UpdateChecker.Instance.CurrentVersion);
            if (hostKey != null) socket.Options.SetRequestHeader("X-Host-Key", hostKey);

            var url = $"{SessionClient.EndpointFor(roomId)}/?roomId={roomId}&role={role}&userId={Guid.NewGuid()}";

            try
            {
                await socket.ConnectAsync(new Uri(url), cts.Token);

                var buffer = new byte[8 * 1024];
                var result = await socket.ReceiveAsync(buffer, cts.Token);

                if (result.MessageType == WebSocketMessageType.Close) return false;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);

                var rejected = doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("type", out var type)
                    && type.GetString() == "room_not_found";

                return !rejected;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] room probe failed ({role}): {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe", CancellationToken.None);
                }
                catch { }
            }
        }
    }
}
