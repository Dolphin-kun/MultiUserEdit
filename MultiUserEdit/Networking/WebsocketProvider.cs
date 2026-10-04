using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

        private static readonly HttpMessageInvoker NoDelayInvoker = CreateNoDelayInvoker();

        private static HttpMessageInvoker CreateNoDelayInvoker()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            return new HttpMessageInvoker(handler);
        }

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
                socket.Options.HttpVersion = HttpVersion.Version11;
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
                    await socket.ConnectAsync(uri, NoDelayInvoker, CancellationToken.None);
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

        public Task SendToManyAsync(IReadOnlyList<string> targetIds, object data)
        {
            if (targetIds.Count == 0) return Task.CompletedTask;
            if (targetIds.Count == 1) return SendAsync(targetIds[0], data);

            return SendCoreAsync(data, null, targetIds);
        }

        public Task SendAsync(string? targetId, object data) => SendCoreAsync(data, targetId, null);

        private readonly ArrayBufferWriter<byte> sendBuffer = new(8192);

        private async Task SendCoreAsync(object data, string? targetId, IReadOnlyList<string>? targetIds)
        {
            if (webSocket?.State != WebSocketState.Open) return;

            await sendLock.WaitAsync();
            try
            {
                var socket = webSocket;
                if (socket?.State != WebSocketState.Open) return;

                sendBuffer.ResetWrittenCount();
                WritePayload(sendBuffer, data, targetId, targetIds);

                await socket.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                sendLock.Release();
            }
        }

        private void WritePayload(IBufferWriter<byte> target, object data, string? targetId, IReadOnlyList<string>? targetIds)
        {
            using var writer = new Utf8JsonWriter(target);

            writer.WriteStartObject();
            writer.WriteString("senderId", userId);

            if (targetId == null) writer.WriteNull("targetId");
            else writer.WriteString("targetId", targetId);

            if (targetIds == null)
            {
                writer.WriteNull("targetIds");
            }
            else
            {
                writer.WriteStartArray("targetIds");
                foreach (var id in targetIds) writer.WriteStringValue(id);
                writer.WriteEndArray();
            }

            writer.WritePropertyName("data");

            if (data is EditEvent editEvent) JsonSerializer.Serialize(writer, editEvent);
            else JsonSerializer.Serialize(writer, data, data.GetType());

            writer.WriteEndObject();
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

                        if (IsBatch(root))
                        {
                            foreach (var item in root.GetProperty("data").GetProperty("items").EnumerateArray())
                            {
                                if (HandlePayload(item, ref roomNotFound, ref roomNotFoundReason, ref roomNotFoundLatestVersion)) return;
                            }
                            continue;
                        }

                        if (HandlePayload(root, ref roomNotFound, ref roomNotFoundReason, ref roomNotFoundLatestVersion)) return;
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

        private static bool IsBatch(JsonElement root) =>
            root.TryGetProperty("senderId", out var sender)
            && sender.GetString() == "server"
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("type", out var type)
            && type.GetString() == "batch"
            && data.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array;

        private bool HandlePayload(JsonElement root, ref bool roomNotFound, ref string? roomNotFoundReason, ref string? roomNotFoundLatestVersion)
        {
            if (!root.TryGetProperty("senderId", out var senderIdProp)) return false;
            if (!root.TryGetProperty("data", out var dataProp)) return false;

            var senderId = senderIdProp.GetString() ?? "unknown";

            if (senderId == userId) return false;

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
                    return true;
                }
                if (senderId == "server" && typeStr == "update_available")
                {
                    UpdateAvailable?.Invoke(dataProp.TryGetProperty("latestVersion", out var availableProp)
                        ? availableProp.GetString()
                        : null);
                    return false;
                }
                if (senderId == "server" && typeStr == "peer_disconnected")
                {
                    if (dataProp.TryGetProperty("userId", out var userIdProp) &&
                        Guid.TryParse(userIdProp.GetString(), out var peerUserId))
                    {
                        var peerIsHost = dataProp.TryGetProperty("isHost", out var isHostProp) && isHostProp.GetBoolean();
                        PeerDisconnected?.Invoke(peerUserId, peerIsHost);
                    }
                    return false;
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

            return false;
        }
    }
}
