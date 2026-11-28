using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
        public event Action<string?>? RoomInfoReceived;
        public event Action<bool, bool>? KeptRoomInfoReceived;
        public event Action? SnapshotRequested;
        public event Action? ResyncRequired;

        private int unackedEdits;

        public bool HasUnacknowledgedEdits => Volatile.Read(ref unackedEdits) > 0;

        private const long MaxIncomingMessageBytes = 64L * 1024 * 1024;

        private static readonly HttpMessageInvoker NoDelayInvoker = CreateNoDelayInvoker();

        private static HttpMessageInvoker CreateNoDelayInvoker() => new(HappyEyeballs.CreateHandler());

        private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(50);
        private const int MaxBatchItems = 256;
        private const int MaxBatchBytes = 512 * 1024;
        private const int MaxEnvelopeDepth = 2;

        private readonly bool coalesce;

        private sealed class Outgoing(object data, string? targetId, IReadOnlyList<string>? targetIds, int generation, bool coalescible)
        {
            public object Data { get; } = data;
            public string? TargetId { get; } = targetId;
            public IReadOnlyList<string>? TargetIds { get; } = targetIds;
            public int Generation { get; } = generation;
            public bool Coalescible { get; } = coalescible;
            public DateTime QueuedAt { get; } = DateTime.UtcNow;
            public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly Channel<Outgoing> outgoing =
            Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });

        public WebsocketProvider(bool coalesce = false) : this(Guid.NewGuid().ToString(), coalesce)
        {
        }

        public WebsocketProvider(string userId, bool coalesce = false)
        {
            this.userId = userId;
            this.coalesce = coalesce;
            _ = Task.Run(WriteLoopAsync);
        }

        public async Task ConnectAsync(string url, IReadOnlyDictionary<string, string> headers)
        {
            await connectionLock.WaitAsync();
            try
            {
                await DisconnectCoreAsync();

                var myGeneration = Interlocked.Increment(ref generation);
                Volatile.Write(ref unackedEdits, 0);

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

        private Task SendCoreAsync(object data, string? targetId, IReadOnlyList<string>? targetIds)
        {
            if (webSocket?.State != WebSocketState.Open) return Task.CompletedTask;

            var item = new Outgoing(data, targetId, targetIds, Volatile.Read(ref generation), coalesce && IsCoalescible(data));
            return outgoing.Writer.TryWrite(item) ? item.Done.Task : Task.CompletedTask;
        }

        private static bool IsCoalescible(object data) =>
            data is EditEvent and not (UserLeftEvent or FileTransferStartEvent or FileChunkEvent or SyncScenesChunkEvent);

        private static bool SameRoute(Outgoing a, Outgoing b)
        {
            if (a.TargetId != b.TargetId) return false;
            if (a.TargetIds == null || b.TargetIds == null) return a.TargetIds == null && b.TargetIds == null;

            return a.TargetIds.SequenceEqual(b.TargetIds);
        }

        private async Task WriteLoopAsync()
        {
            var reader = outgoing.Reader;
            var batch = new List<Outgoing>();

            while (await reader.WaitToReadAsync())
            {
                if (!reader.TryRead(out var first)) continue;

                batch.Clear();
                batch.Add(first);

                if (first.Coalescible)
                {
                    var wait = BatchWindow - (DateTime.UtcNow - first.QueuedAt);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);

                    while (batch.Count < MaxBatchItems
                           && reader.TryPeek(out var next)
                           && next.Coalescible
                           && next.Generation == first.Generation
                           && SameRoute(first, next)
                           && reader.TryRead(out next))
                    {
                        batch.Add(next);
                    }
                }

                try
                {
                    await WriteAsync(batch);
                }
                catch (WebSocketException) { }
                catch (ObjectDisposedException) { }
                catch
                {
                }
                finally
                {
                    foreach (var item in batch) item.Done.TrySetResult();
                }
            }
        }

        private async Task WriteAsync(List<Outgoing> batch)
        {
            var first = batch[0];
            if (first.Generation != Volatile.Read(ref generation)) return;

            var socket = webSocket;
            if (socket?.State != WebSocketState.Open) return;

            var (edits, cursor) = Condense(batch);

            var payloads = new List<byte[]>(edits.Count);
            foreach (var item in edits) payloads.Add(Serialize(item.Data, item.TargetId, item.TargetIds));

            var start = 0;
            while (start < payloads.Count)
            {
                var end = start;
                var size = 0;
                while (end < payloads.Count && (end == start || size + payloads[end].Length <= MaxBatchBytes))
                {
                    size += payloads[end].Length;
                    end++;
                }

                var message = end - start == 1 ? payloads[start] : BuildBatch(first, payloads, start, end);
                var projectEdits = first.TargetId == null && first.TargetIds == null
                    ? edits.Skip(start).Take(end - start).Count(item => item.Data is EditEvent edit && Commons.SyncScenesTransfer.IsProjectEdit(edit))
                    : 0;
                if (projectEdits > 0) Interlocked.Add(ref unackedEdits, projectEdits);

                await socket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);
                start = end;
            }

            if (cursor != null)
                await socket.SendAsync(Serialize(cursor.Data, cursor.TargetId, cursor.TargetIds), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static (List<Outgoing> Edits, Outgoing? Cursor) Condense(List<Outgoing> batch)
        {
            Outgoing? cursor = null;
            var seenMoves = new HashSet<Guid>();
            var kept = new List<Outgoing>(batch.Count);

            for (var i = batch.Count - 1; i >= 0; i--)
            {
                var item = batch[i];

                if (item.Data is CursorMovedEvent)
                {
                    cursor ??= item;
                    continue;
                }

                if (item.Data is ItemMovedEvent moved && !seenMoves.Add(moved.ItemId)) continue;

                kept.Add(item);
            }

            kept.Reverse();
            return (kept, cursor);
        }

        private byte[] Serialize(object data, string? targetId, IReadOnlyList<string>? targetIds)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            WritePayload(buffer, data, targetId, targetIds);
            return buffer.WrittenSpan.ToArray();
        }

        private byte[] BuildBatch(Outgoing route, List<byte[]> payloads, int start, int end)
        {
            var buffer = new ArrayBufferWriter<byte>(payloads.Skip(start).Take(end - start).Sum(p => p.Length) + 256);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("senderId", userId);

                if (route.TargetId == null) writer.WriteNull("targetId");
                else writer.WriteString("targetId", route.TargetId);

                if (route.TargetIds == null)
                {
                    writer.WriteNull("targetIds");
                }
                else
                {
                    writer.WriteStartArray("targetIds");
                    foreach (var id in route.TargetIds) writer.WriteStringValue(id);
                    writer.WriteEndArray();
                }

                writer.WriteStartObject("data");
                writer.WriteString("type", "batch");
                writer.WriteStartArray("items");
                for (var i = start; i < end; i++) writer.WriteRawValue(payloads[i], skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
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
                    var oversized = false;

                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            serverDisconnected = true;
                            return;
                        }

                        if (result.MessageType != WebSocketMessageType.Text) continue;

                        if (oversized || result.Count <= 0) continue;

                        if (ms.Length + result.Count > MaxIncomingMessageBytes)
                        {
                            oversized = true;
                            ms.SetLength(0);
                            continue;
                        }

                        ms.Write(buffer, 0, result.Count);

                    } while (!result.EndOfMessage);

                    if (!IsCurrent()) return;

                    if (oversized)
                    {
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(ms.ToArray());

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (HandleEnvelope(root, 0, ref roomNotFound, ref roomNotFoundReason, ref roomNotFoundLatestVersion)) return;
                    }
                    catch (JsonException)
                    {
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

        private static bool IsBatch(JsonElement root, out string? sender, out JsonElement items)
        {
            sender = null;
            items = default;

            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("senderId", out var senderProp) || senderProp.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "batch") return false;
            if (!data.TryGetProperty("items", out items) || items.ValueKind != JsonValueKind.Array) return false;

            sender = senderProp.GetString();
            return sender != null;
        }

        private bool HandleEnvelope(JsonElement root, int depth, ref bool roomNotFound, ref string? roomNotFoundReason, ref string? roomNotFoundLatestVersion)
        {
            if (!IsBatch(root, out var sender, out var items))
            {
                var stop = HandlePayload(root, ref roomNotFound, ref roomNotFoundReason, ref roomNotFoundLatestVersion);
                if (depth == 0) NoteSequence(root);
                return stop;
            }

            if (depth >= MaxEnvelopeDepth) return false;

            foreach (var item in items.EnumerateArray())
            {
                if (sender != "server")
                {
                    if (!item.TryGetProperty("senderId", out var inner) || inner.GetString() != sender) continue;
                    if (IsBatch(item, out _, out _)) continue;
                }

                if (HandleEnvelope(item, depth + 1, ref roomNotFound, ref roomNotFoundReason, ref roomNotFoundLatestVersion)) return true;
            }

            if (depth == 0) NoteSequence(root);
            return false;
        }

        private void NoteSequence(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("seq", out var seqProp)
                && seqProp.TryGetInt64(out var seq)
                && seq > 0)
            {
                EventReceived?.Invoke(this, new SequenceMarkerEvent(seq));
            }
        }

        private static bool IsReplay(JsonElement root) =>
            root.TryGetProperty("replay", out var replay) && replay.ValueKind == JsonValueKind.True;

        private bool HandlePayload(JsonElement root, ref bool roomNotFound, ref string? roomNotFoundReason, ref string? roomNotFoundLatestVersion)
        {
            if (!root.TryGetProperty("senderId", out var senderIdProp)) return false;
            if (!root.TryGetProperty("data", out var dataProp)) return false;

            var senderId = senderIdProp.GetString() ?? "unknown";

            var isReplay = IsReplay(root);
            if (senderId == userId && !isReplay) return false;

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
                if (senderId == "server" && typeStr == "system:room_info")
                {
                    RoomInfoReceived?.Invoke(dataProp.TryGetProperty("hostUserId", out var hostProp)
                        ? hostProp.GetString()
                        : null);

                    if (dataProp.TryGetProperty("persistent", out var persistentProp) && persistentProp.ValueKind == JsonValueKind.True)
                    {
                        var hasSnapshot = dataProp.TryGetProperty("hasSnapshot", out var snapshotProp) && snapshotProp.ValueKind == JsonValueKind.True;
                        KeptRoomInfoReceived?.Invoke(true, hasSnapshot);
                    }
                    return false;
                }
                if (senderId == "server" && typeStr == "system:host_changed")
                {
                    RoomInfoReceived?.Invoke(dataProp.TryGetProperty("hostUserId", out var changedProp)
                        ? changedProp.GetString()
                        : null);
                    return false;
                }
                if (senderId == "server" && typeStr == "system:seq")
                {
                    var acknowledged = dataProp.TryGetProperty("count", out var countProp) && countProp.TryGetInt32(out var count) ? count : 0;
                    if (acknowledged > 0)
                    {
                        var remaining = Interlocked.Add(ref unackedEdits, -acknowledged);
                        if (remaining < 0) Interlocked.CompareExchange(ref unackedEdits, 0, remaining);
                    }

                    if (dataProp.TryGetProperty("seq", out var ackSeqProp) && ackSeqProp.TryGetInt64(out var ackSeq) && ackSeq > 0)
                        EventReceived?.Invoke(this, new SequenceMarkerEvent(ackSeq));
                    return false;
                }
                if (senderId == "server" && typeStr == "system:snapshot_request")
                {
                    SnapshotRequested?.Invoke();
                    return false;
                }
                if (senderId == "server" && typeStr == "system:resync_required")
                {
                    ResyncRequired?.Invoke();
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
                        if (senderId == "server")
                        {
                            editEvent = editEvent with { ExecutorId = SessionClient.ServerUserId };
                        }
                        else if (Guid.TryParse(senderId, out var executorGuid))
                        {
                            editEvent = editEvent with { ExecutorId = executorGuid };
                        }

                        EventReceived?.Invoke(this, isReplay ? new ReplayedEvent(editEvent) : editEvent);
                    }
                }
                catch (Exception ex)
                {

                    if (ex.Message.Contains("discriminator", StringComparison.OrdinalIgnoreCase)) return false;

                    Commons.ErrorNotifier.NotifyOnce(
                        Texts.PeerDataUnreadableTitle,
                        Texts.PeerDataUnreadableMessage);
                }
            }

            return false;
        }
    }
}
