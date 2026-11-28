using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Events;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Networking
{
    internal enum PeerSendResult
    {
        Completed,
        NotConnected,
        Interrupted,
        Cancelled,
    }

    internal sealed record PeerTransferStart(Guid TransferId, string FileName, long FileSize, int TotalChunks, int ChunkSize, string? Hash, string? CharacterName);

    internal sealed class PeerTransport : IDisposable
    {
        internal static readonly string[] IceServers = ["stun:stun.l.google.com:19302", "stun:stun.cloudflare.com:3478"];
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan IdleLinkLifetime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);
        private const int MaxBuffered = 4 * 1024 * 1024;
        private const int MaxWindowChunks = 256;
        private const int MaxMessageSize = 1024 * 1024;

        private static readonly Lazy<bool> available = new(() =>
        {
            var loaded = RtcNative.TryLoad(out var error);
            InitializationError = error;
            return loaded;
        });

        public static bool IsAvailable => available.Value;

        public static string? InitializationError { get; private set; }

        private readonly Func<Guid, RtcSignalEvent, Task> sendSignal;
        private readonly ConcurrentDictionary<Guid, Link> outgoing = new();
        private readonly ConcurrentDictionary<Guid, Link> incoming = new();
        private readonly ConcurrentDictionary<Guid, List<RtcSignalEvent>> earlySignals = new();
        private readonly ConcurrentDictionary<Guid, Deferred> deferred = new();
        private readonly Timer sweeper;
        private readonly DisposeCollector disposer = new();

        public Func<Guid, PeerTransferStart, Func<int, bool>, bool>? AcceptTransfer { get; set; }
        public Action<Guid, Guid, int, byte[]>? ReceiveChunk { get; set; }
        public Action<Guid, Guid, int>? ReceiveAck { get; set; }
        public Func<Guid, Guid, bool>? IsCancelled { get; set; }
        public Func<Guid, bool?>? AllowIncoming { get; set; }

        public PeerTransport(Func<Guid, RtcSignalEvent, Task> sendSignal)
        {
            this.sendSignal = sendSignal;
            sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            disposer.CollectAction(nameof(outgoing), () =>
            {
                foreach (var link in outgoing.Values.Concat(incoming.Values)) link.Dispose();
                outgoing.Clear();
                incoming.Clear();
            });
            disposer.Collect(sweeper);
        }

        public void HandleSignal(Guid from, RtcSignalEvent signal)
        {
            if (disposer.IsDisposed) return;
            try
            {
                if (outgoing.TryGetValue(from, out var mine) && mine.SessionId == signal.SessionId)
                {
                    mine.Apply(signal);
                    return;
                }

                if (incoming.TryGetValue(signal.SessionId, out var theirs))
                {
                    theirs.Apply(signal);
                    return;
                }

                if (deferred.TryGetValue(signal.SessionId, out var waiting))
                {
                    lock (waiting.Signals) if (waiting.Signals.Count < 64) waiting.Signals.Add(signal);
                    return;
                }

                if (signal.Kind != "offer")
                {
                    var queue = earlySignals.GetOrAdd(signal.SessionId, _ => []);
                    lock (queue) if (queue.Count < 64) queue.Add(signal);
                    return;
                }

                switch (AllowIncoming == null ? true : AllowIncoming(from))
                {
                    case false:
                        Reject(from, signal.SessionId);
                        return;
                    case null:
                        var entry = new Deferred(from, DateTime.UtcNow);
                        entry.Signals.Add(signal);
                        if (earlySignals.TryRemove(signal.SessionId, out var before))
                        {
                            lock (before) entry.Signals.AddRange(before);
                        }
                        deferred[signal.SessionId] = entry;
                        _ = sendSignal(from, new RtcSignalEvent(signal.SessionId, "hold", string.Empty, null) { DateTime = DateTime.UtcNow });
                        return;
                }

                var link = new Link(this, from, signal.SessionId, isOfferer: false);
                incoming[signal.SessionId] = link;
                link.Apply(signal);
                if (earlySignals.TryRemove(signal.SessionId, out var pending))
                {
                    lock (pending) foreach (var early in pending) link.Apply(early);
                }
            }
            catch
            {
            }
        }

        public void ResolveDeferred(bool allow)
        {
            foreach (var pair in deferred.ToList())
            {
                if (!deferred.TryRemove(pair)) continue;
                if (!allow)
                {
                    Reject(pair.Value.From, pair.Key);
                    continue;
                }
                List<RtcSignalEvent> signals;
                lock (pair.Value.Signals) signals = [.. pair.Value.Signals];
                foreach (var signal in signals) HandleSignal(pair.Value.From, signal);
            }
        }

        private void Reject(Guid from, Guid sessionId) =>
            _ = sendSignal(from, new RtcSignalEvent(sessionId, "reject", string.Empty, null) { DateTime = DateTime.UtcNow });

        public void Forget(Guid peer)
        {
            foreach (var pair in deferred.Where(p => p.Value.From == peer).ToList()) deferred.TryRemove(pair);
            if (outgoing.TryRemove(peer, out var link)) link.Dispose();
            foreach (var pair in incoming.Where(p => p.Value.Remote == peer).ToList())
            {
                if (incoming.TryRemove(pair.Key, out var other)) other.Dispose();
            }
        }

        public async Task<(PeerSendResult Result, int NextChunk)> SendFileAsync(Guid remote, PeerTransferStart start, string filePath, Action<long>? progress)
        {
            if (disposer.IsDisposed || !IsAvailable) return (PeerSendResult.NotConnected, 0);

            Link link;
            try
            {
                link = outgoing.GetOrAdd(remote, peer => new Link(this, peer, Guid.NewGuid(), isOfferer: true));
            }
            catch
            {
                return (PeerSendResult.NotConnected, 0);
            }

            if (!await link.WaitConnectedAsync(ConnectTimeout))
            {
                if (outgoing.TryRemove(new KeyValuePair<Guid, Link>(remote, link))) link.Dispose();
                return (PeerSendResult.NotConnected, 0);
            }

            var transfer = new OutgoingTransfer(start.TransferId);
            var channel = link.OpenChannel(start.TransferId, transfer);
            if (channel == null || !await transfer.Opened.Task.WaitAsync(ChannelTimeout).ContinueWith(t => t.IsCompletedSuccessfully))
            {
                link.CloseChannel(start.TransferId);
                return (PeerSendResult.NotConnected, 0);
            }

            link.Touch();
            try
            {
                channel.Send(JsonSerializer.Serialize(new { t = "start", start.FileName, start.FileSize, start.TotalChunks, start.ChunkSize, start.Hash, start.CharacterName }));
                var buffer = new byte[start.ChunkSize + 4];
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, start.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

                for (var index = 0; index < start.TotalChunks; index++)
                {
                    var stalledSince = DateTime.UtcNow;
                    var lastAck = transfer.LastAck;
                    while (index - transfer.LastAck > transfer.Pacer.Window || channel.BufferedAmount > MaxBuffered)
                    {
                        if (transfer.Declined) return (PeerSendResult.Cancelled, index);
                        if (transfer.Closed || link.IsBroken) return (PeerSendResult.Interrupted, transfer.LastAck + 1);
                        if (IsCancelled?.Invoke(start.TransferId, remote) == true) return (PeerSendResult.Cancelled, index);
                        if (transfer.LastAck != lastAck)
                        {
                            lastAck = transfer.LastAck;
                            stalledSince = DateTime.UtcNow;
                        }
                        else if (DateTime.UtcNow - stalledSince > StallTimeout)
                        {
                            return (PeerSendResult.Interrupted, transfer.LastAck + 1);
                        }
                        await Task.Delay(5);
                    }
                    if (transfer.Declined) return (PeerSendResult.Cancelled, index);
                    if (transfer.Closed || link.IsBroken) return (PeerSendResult.Interrupted, transfer.LastAck + 1);
                    if (IsCancelled?.Invoke(start.TransferId, remote) == true) return (PeerSendResult.Cancelled, index);

                    var read = await stream.ReadAtLeastAsync(buffer.AsMemory(4, start.ChunkSize), start.ChunkSize, throwOnEndOfStream: false);
                    BinaryPrimitives.WriteInt32LittleEndian(buffer, index);
                    transfer.Pacer.Sent(index);
                    if (!channel.Send(buffer.AsSpan(0, read + 4))) return (PeerSendResult.Interrupted, transfer.LastAck + 1);
                    progress?.Invoke(read);
                    link.Touch();
                }

                var waitStarted = DateTime.UtcNow;
                var seen = transfer.LastAck;
                while (transfer.LastAck < start.TotalChunks - 1)
                {
                    if (transfer.Declined) return (PeerSendResult.Cancelled, start.TotalChunks);
                    if (transfer.Closed || link.IsBroken) return (PeerSendResult.Interrupted, transfer.LastAck + 1);
                    if (IsCancelled?.Invoke(start.TransferId, remote) == true) return (PeerSendResult.Cancelled, start.TotalChunks);
                    if (transfer.LastAck != seen)
                    {
                        seen = transfer.LastAck;
                        waitStarted = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - waitStarted > StallTimeout)
                    {
                        return (PeerSendResult.Interrupted, transfer.LastAck + 1);
                    }
                    await Task.Delay(20);
                }
                return (PeerSendResult.Completed, start.TotalChunks);
            }
            catch
            {
                return (PeerSendResult.Interrupted, transfer.LastAck + 1);
            }
            finally
            {
                link.CloseChannel(start.TransferId);
                link.Touch();
            }
        }

        private void Sweep()
        {
            foreach (var pair in outgoing.ToList())
            {
                if (!pair.Value.IsIdle(IdleLinkLifetime) && !pair.Value.IsBroken) continue;
                if (outgoing.TryRemove(pair)) pair.Value.Dispose();
            }
            foreach (var pair in incoming.ToList())
            {
                if (!pair.Value.IsIdle(IdleLinkLifetime) && !pair.Value.IsBroken) continue;
                if (incoming.TryRemove(pair)) pair.Value.Dispose();
            }
            earlySignals.Clear();
            foreach (var pair in deferred.ToList())
            {
                if (DateTime.UtcNow - pair.Value.Since > IdleLinkLifetime) deferred.TryRemove(pair);
            }
        }

        private sealed record Deferred(Guid From, DateTime Since)
        {
            public List<RtcSignalEvent> Signals { get; } = [];
        }

        public void Dispose() => disposer.Dispose();

        private sealed class OutgoingTransfer(Guid transferId)
        {
            private int lastAck = -1;

            public Guid TransferId { get; } = transferId;
            public TransferPacer Pacer { get; } = new(MaxWindowChunks);
            public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int LastAck => Volatile.Read(ref lastAck);
            public bool Closed { get; set; }
            public bool Declined { get; set; }

            public void Ack(int index)
            {
                int current;
                do
                {
                    current = Volatile.Read(ref lastAck);
                    if (index <= current) return;
                } while (Interlocked.CompareExchange(ref lastAck, index, current) != current);
            }
        }

        private sealed class Link : IDisposable
        {
            private readonly PeerTransport owner;
            private readonly RtcConnection connection;
            private readonly ConcurrentDictionary<Guid, RtcChannel> channels = new();
            private readonly ConcurrentDictionary<Guid, OutgoingTransfer> sending = new();
            private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Lock gate = new();
            private RtcChannel? control;
            private long lastUsed = DateTime.UtcNow.Ticks;
            private long heldUntil;
            private bool disposed;

            public Guid Remote { get; }
            public Guid SessionId { get; }
            public bool IsBroken { get; private set; }

            public Link(PeerTransport owner, Guid remote, Guid sessionId, bool isOfferer)
            {
                this.owner = owner;
                Remote = remote;
                SessionId = sessionId;
                connection = new RtcConnection(IceServers, MaxMessageSize);
                connection.LocalDescription += (sdp, type) => Signal(type, sdp, null);
                connection.LocalCandidate += (candidate, mid) => Signal("candidate", candidate, mid);
                connection.StateChanged += state =>
                {
                    if (state == RtcNative.StateConnected) connected.TrySetResult();
                    if (state is RtcNative.StateFailed or RtcNative.StateClosed or RtcNative.StateDisconnected)
                    {
                        IsBroken = true;
                        connected.TrySetResult();
                        foreach (var transfer in sending.Values) transfer.Closed = true;
                    }
                };
                connection.ChannelOpened += Accept;

                if (isOfferer) control = connection.CreateChannel("link");
            }

            private void Signal(string kind, string data, string? mid)
            {
                if (disposed) return;
                _ = owner.sendSignal(Remote, new RtcSignalEvent(SessionId, kind, data, mid) { DateTime = DateTime.UtcNow });
            }

            public void Apply(RtcSignalEvent signal)
            {
                if (disposed) return;
                switch (signal.Kind)
                {
                    case "offer":
                    case "answer":
                        connection.SetRemoteDescription(signal.Data, signal.Kind);
                        break;
                    case "candidate":
                        connection.AddRemoteCandidate(signal.Data, signal.Mid);
                        break;
                    case "hold":
                        Interlocked.Exchange(ref heldUntil, (DateTime.UtcNow + HoldLimit).Ticks);
                        break;
                    case "reject":
                        IsBroken = true;
                        connected.TrySetResult();
                        break;
                }
            }

            public async Task<bool> WaitConnectedAsync(TimeSpan timeout)
            {
                var until = (DateTime.UtcNow + timeout).Ticks;
                while (!connected.Task.IsCompleted && !disposed)
                {
                    var left = TimeSpan.FromTicks(Math.Max(until, Interlocked.Read(ref heldUntil)) - DateTime.UtcNow.Ticks);
                    if (left <= TimeSpan.Zero) break;
                    await Task.WhenAny(connected.Task, Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1)));
                }
                return connected.Task.IsCompleted && !IsBroken && !disposed;
            }

            public void Touch() => Interlocked.Exchange(ref lastUsed, DateTime.UtcNow.Ticks);

            public bool IsIdle(TimeSpan lifetime) =>
                sending.IsEmpty && channels.IsEmpty && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastUsed) > lifetime.Ticks;

            public RtcChannel? OpenChannel(Guid transferId, OutgoingTransfer transfer)
            {
                lock (gate)
                {
                    if (disposed) return null;
                    var channel = connection.CreateChannel(transferId.ToString());
                    if (channel == null) return null;
                    sending[transferId] = transfer;
                    channel.Open += () => transfer.Opened.TrySetResult();
                    channel.Close += () => transfer.Closed = true;
                    channel.Text += text => HandleSenderText(transfer, text);
                    channels[transferId] = channel;
                    if (channel.IsOpen) transfer.Opened.TrySetResult();
                    return channel;
                }
            }

            private void HandleSenderText(OutgoingTransfer transfer, string text)
            {
                try
                {
                    using var json = JsonDocument.Parse(text);
                    var root = json.RootElement;
                    var kind = root.GetProperty("t").GetString();
                    if (kind == "declined")
                    {
                        transfer.Declined = true;
                        transfer.Closed = true;
                        return;
                    }
                    if (kind == "ack")
                    {
                        var index = root.GetProperty("i").GetInt32();
                        transfer.Ack(index);
                        transfer.Pacer.Acked(Remote, index);
                        owner.ReceiveAck?.Invoke(Remote, transfer.TransferId, index);
                    }
                }
                catch
                {
                }
            }

            public void CloseChannel(Guid transferId)
            {
                sending.TryRemove(transferId, out _);
                if (channels.TryRemove(transferId, out var channel))
                {
                    try { channel.Dispose(); } catch { }
                }
            }

            private void Accept(RtcChannel channel)
            {
                if (disposed || !Guid.TryParse(channel.Label, out var transferId))
                {
                    if (!disposed && channel.Label == "link") control = channel;
                    else _ = Task.Run(channel.Dispose);
                    return;
                }

                channels[transferId] = channel;
                Touch();
                channel.Text += text =>
                {
                    try
                    {
                        using var json = JsonDocument.Parse(text);
                        var root = json.RootElement;
                        if (root.GetProperty("t").GetString() != "start") return;
                        var start = new PeerTransferStart(
                            transferId,
                            root.GetProperty("FileName").GetString() ?? string.Empty,
                            root.GetProperty("FileSize").GetInt64(),
                            root.GetProperty("TotalChunks").GetInt32(),
                            root.GetProperty("ChunkSize").GetInt32(),
                            root.TryGetProperty("Hash", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null,
                            root.TryGetProperty("CharacterName", out var character) && character.ValueKind == JsonValueKind.String ? character.GetString() : null);
                        var accepted = owner.AcceptTransfer?.Invoke(Remote, start, index => SendAck(channel, index)) ?? false;
                        if (!accepted) channel.Send("{\"t\":\"declined\"}");
                    }
                    catch
                    {
                    }
                };
                channel.Binary += data =>
                {
                    if (data.Length < 4) return;
                    var index = BinaryPrimitives.ReadInt32LittleEndian(data);
                    owner.ReceiveChunk?.Invoke(Remote, transferId, index, data[4..]);
                    Touch();
                };
                channel.Close += () =>
                {
                    if (channels.TryRemove(transferId, out var closed)) _ = Task.Run(closed.Dispose);
                };
            }

            private static bool SendAck(RtcChannel channel, int index)
            {
                try
                {
                    return channel.IsOpen && channel.Send($"{{\"t\":\"ack\",\"i\":{index}}}");
                }
                catch
                {
                    return false;
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                }
                foreach (var transfer in sending.Values) transfer.Closed = true;
                foreach (var channel in channels.Values)
                {
                    try { channel.Dispose(); } catch { }
                }
                channels.Clear();
                try { control?.Dispose(); } catch { }
                try { connection.Dispose(); } catch { }
                connected.TrySetResult();
            }
        }
    }
}
