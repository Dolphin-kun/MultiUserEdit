using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MultiUserEdit.Commons.Events;

namespace MultiUserEdit.Networking
{
    internal class WebsocketProvider : INetworkProvider
    {
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);

        private ClientWebSocket? webSocket;
        private CancellationTokenSource? cts;
        private Task? receiveTask;
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly SemaphoreSlim connectionLock = new(1, 1);

        private int generation;

        private readonly string userId;
        private string? roomId;

        public string LocalUserId => userId;
        public string? RoomId => roomId;

        public event EventHandler<EditEvent>? EventReceived;
        public event Action? Disconnected;
        public event Action<string?, string?>? RoomNotFound;
        public event Action<string?>? UpdateAvailable;
        public event Action<Guid, bool>? PeerDisconnected;

        public WebsocketProvider()
        {
            userId = Guid.NewGuid().ToString();
        }

        public async Task ConnectAsync(string url, IReadOnlyDictionary<string, string> headers)
        {
            await connectionLock.WaitAsync();
            try
            {
                await DisconnectCoreAsync();

                var myGeneration = Interlocked.Increment(ref generation);

                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = KeepAliveInterval;
                socket.Options.KeepAliveTimeout = KeepAliveTimeout;
                foreach (var (name, value) in headers)
                    socket.Options.SetRequestHeader(name, value);

                var source = new CancellationTokenSource();
                webSocket = socket;
                cts = source;

                var uri = new Uri(url);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                roomId = query["roomId"];

                try
                {
                    await socket.ConnectAsync(uri, CancellationToken.None);
                }
                catch
                {
                    webSocket = null;
                    cts = null;
                    socket.Dispose();
                    source.Dispose();
                    throw;
                }

                receiveTask = ReceiveLoopAsync(socket, source, myGeneration);
            }
            finally
            {
                connectionLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await connectionLock.WaitAsync();
            try
            {
                await DisconnectCoreAsync();
            }
            finally
            {
                connectionLock.Release();
            }
        }

        private async Task DisconnectCoreAsync()
        {
            Interlocked.Increment(ref generation);

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
                var socket = webSocket;
                if (socket?.State != WebSocketState.Open) return;
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationTokenSource source, int myGeneration)
        {
            var token = source.Token;
            var buffer = new byte[64 * 1024];
            bool serverDisconnected = false;
            bool roomNotFound = false;
            string? roomNotFoundReason = null;
            string? roomNotFoundLatestVersion = null;

            bool IsCurrent() => Volatile.Read(ref generation) == myGeneration;

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

                    if (!IsCurrent()) return;

                    var json = Encoding.UTF8.GetString(ms.ToArray());

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("senderId", out var senderIdProp)) continue;
                        if (!root.TryGetProperty("data", out var dataProp)) continue;

                        var senderId = senderIdProp.GetString() ?? "unknown";

                        if (senderId == userId) continue;

                        var dataJson = dataProp.GetRawText();

                        if (dataProp.ValueKind == JsonValueKind.Object &&
                            dataProp.TryGetProperty("type", out var typeProp))
                        {
                            var typeStr = typeProp.GetString();
                            if (senderId == "server" && typeStr == "room_not_found")
                            {
                                roomNotFound = true;
                                roomNotFoundReason = dataProp.TryGetProperty("reason", out var reasonProp)
                                    ? reasonProp.GetString()
                                    : null;
                                roomNotFoundLatestVersion = dataProp.TryGetProperty("latestVersion", out var latestProp)
                                    ? latestProp.GetString()
                                    : null;
                                return;
                            }
                            if (senderId == "server" && typeStr == "update_available")
                            {
                                UpdateAvailable?.Invoke(dataProp.TryGetProperty("latestVersion", out var availableProp)
                                    ? availableProp.GetString()
                                    : null);
                                continue;
                            }
                            if (senderId == "server" && typeStr == "peer_disconnected")
                            {
                                if (dataProp.TryGetProperty("userId", out var userIdProp) &&
                                    Guid.TryParse(userIdProp.GetString(), out var peerUserId))
                                {
                                    var peerIsHost = dataProp.TryGetProperty("isHost", out var isHostProp) && isHostProp.GetBoolean();
                                    PeerDisconnected?.Invoke(peerUserId, peerIsHost);
                                }
                                continue;
                            }
                        }

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
                if (IsCurrent())
                {
                    if (roomNotFound)
                    {
                        RoomNotFound?.Invoke(roomNotFoundReason, roomNotFoundLatestVersion);
                    }
                    else if (serverDisconnected)
                    {
                        Disconnected?.Invoke();
                    }
                }
            }
        }
    }
}
