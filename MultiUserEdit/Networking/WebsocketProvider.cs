using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    internal class WebsocketProvider : INetworkProvider
    {
        private ClientWebSocket? webSocket;
        private CancellationTokenSource? cts;
        private Task? receiveTask;
        // ClientWebSocket.SendAsyncは同一インスタンスへの同時呼び出しを許容していない
        // （複数の動画を同時に送信する等でチャンク送信が重なると1つ以上のタスクが例外になりうる）ため、
        // 送信だけを直列化する
        private readonly SemaphoreSlim sendLock = new(1, 1);

        private readonly string userId;
        private string? roomId;

        public string LocalUserId => userId;
        public string? RoomId => roomId;

        public event EventHandler<EditEvent>? EventReceived;
        public event Action? Disconnected;
        public event Action? RoomNotFound;
        public event Action<Guid, bool>? PeerDisconnected;

        public WebsocketProvider()
        {
            userId = Guid.NewGuid().ToString();
        }

        public async Task ConnectAsync(string url)
        {
            if (webSocket != null)
            {
                await DisconnectAsync();
            }

            webSocket = new ClientWebSocket();
            cts = new CancellationTokenSource();

            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            roomId = query["roomId"];

            await webSocket.ConnectAsync(uri, CancellationToken.None);
            receiveTask = ReceiveLoopAsync();
        }

        public async Task DisconnectAsync()
        {
            cts?.Cancel();

            if (receiveTask != null)
            {
                try { await receiveTask; } catch { }
                receiveTask = null;
            }

            if (webSocket != null)
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                    catch { }
                }
                webSocket.Dispose();
                webSocket = null;
            }

            cts?.Dispose();
            cts = null;
        }

        public async Task SendAsync(string? targetId, object data)
        {
            if (webSocket?.State != WebSocketState.Open) return;

            var dataElement = data is EditEvent editEvent
                ? JsonSerializer.SerializeToElement(editEvent)
                : JsonSerializer.SerializeToElement(data, data.GetType());

            var payload = new
            {
                senderId = userId,
                targetId,
                data = dataElement
            };

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);

            await sendLock.WaitAsync();
            try
            {
                if (webSocket?.State != WebSocketState.Open) return;
                await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var socket = webSocket;
            var source = cts;
            if (socket == null || source == null) return;

            var token = source.Token;
            var buffer = new byte[4 * 1024];
            bool serverDisconnected = false;
            bool roomNotFound = false;

            try
            {
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            serverDisconnected = true;
                            return;
                        }

                        if (result.MessageType != WebSocketMessageType.Text) continue;

                        if (result.Count > 0)
                            ms.Write(buffer, 0, result.Count);

                    } while (!result.EndOfMessage);

                    var json = Encoding.UTF8.GetString(ms.ToArray());

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("senderId", out var senderIdProp)) continue;
                        if (!root.TryGetProperty("data", out var dataProp)) continue;

                        var senderId = senderIdProp.GetString() ?? "unknown";

                        // 自分自身が送信したパケット（エコーバック）はネットワーク受領直後に完全カット
                        if (senderId == userId) continue;

                        var dataJson = dataProp.GetRawText();

                        if (dataProp.ValueKind == JsonValueKind.Object &&
                            dataProp.TryGetProperty("type", out var typeProp))
                        {
                            var typeStr = typeProp.GetString();
                            if (senderId == "server" && typeStr == "room_not_found")
                            {
                                // 指定されたルームにホストが一度も接続していない（存在しない）ことをサーバーが通知
                                roomNotFound = true;
                                return;
                            }
                            if (senderId == "server" && typeStr == "peer_disconnected")
                            {
                                // 他ユーザーがアプリ側の離脱通知を送れないまま切断した場合に、サーバーが代わりに通知する
                                if (dataProp.TryGetProperty("userId", out var userIdProp) &&
                                    Guid.TryParse(userIdProp.GetString(), out var peerUserId))
                                {
                                    var peerIsHost = dataProp.TryGetProperty("isHost", out var isHostProp) && isHostProp.GetBoolean();
                                    PeerDisconnected?.Invoke(peerUserId, peerIsHost);
                                }
                                continue;
                            }
                        }

                        // $type プロパティが存在する正規の EditEvent のみデシリアライズを実行（Missing discriminator 例外を回避）
                        if (dataProp.ValueKind == JsonValueKind.Object && dataProp.TryGetProperty("$type", out _))
                        {
                            try
                            {
                                var editEvent = JsonSerializer.Deserialize<EditEvent>(dataJson);
                                if (editEvent != null)
                                {
                                    if (Guid.TryParse(senderId, out var executorGuid))
                                    {
                                        editEvent = editEvent with { ExecutorId = executorGuid };
                                    }

                                    EventReceived?.Invoke(this, editEvent);
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[WebsocketProvider] Event Deserialize Exception: {ex.Message}");
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebsocketProvider] JsonException: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (WebSocketException)
            {
                serverDisconnected = true;
            }
            finally
            {
                if (roomNotFound)
                {
                    RoomNotFound?.Invoke();
                }
                else if (serverDisconnected)
                {
                    Disconnected?.Invoke();
                }
            }
        }
    }
}
