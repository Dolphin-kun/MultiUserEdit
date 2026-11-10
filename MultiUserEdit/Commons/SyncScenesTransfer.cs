using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace MultiUserEdit.Commons
{
    internal sealed class SyncScenesTransfer
    {
        private const int ChunkSize = 256 * 1024;
        private const long MaxTotalBytes = 1024L * 1024 * 1024;
        private const int MaxHeldEvents = 20000;
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan HoldLifetime = TimeSpan.FromMinutes(3);

        private sealed class Pending
        {
            public required byte[]?[] Chunks { get; init; }
            public DateTime StartedAt { get; } = DateTime.UtcNow;
            public int Received { get; set; }
            public long TotalBytes { get; set; }
        }

        private readonly ConcurrentDictionary<Guid, Pending> pending = new();

        private readonly List<EditEvent> held = [];
        private Guid holdingSyncId;
        private DateTime holdingSince;

        public static IReadOnlyList<SyncScenesChunkEvent> Split(Guid syncId, OnlineScenes scenes, bool isManual)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(scenes);
            var totalChunks = Math.Max(1, (int)Math.Ceiling((double)payload.Length / ChunkSize));

            var chunks = new List<SyncScenesChunkEvent>(totalChunks);
            for (var index = 0; index < totalChunks; index++)
            {
                var offset = index * ChunkSize;
                var length = Math.Min(ChunkSize, payload.Length - offset);

                chunks.Add(new SyncScenesChunkEvent(syncId, index, totalChunks,
                    Convert.ToBase64String(payload, offset, length), isManual));
            }

            return chunks;
        }

        public static bool IsProjectEdit(EditEvent editEvent) => editEvent
            is ItemAddedEvent or ItemRemovedEvent or ItemMovedEvent or ItemUpdatedEvent
            or SceneAddedEvent or SceneRemovedEvent or SceneRenamedEvent or VideoInfoUpdatedEvent;

        public void BeginHolding(Guid syncId)
        {
            lock (held)
            {
                held.Clear();
                holdingSyncId = syncId;
                holdingSince = DateTime.UtcNow;
            }
        }

        public bool ShouldRelease
        {
            get
            {
                lock (held)
                {
                    if (holdingSyncId == Guid.Empty) return false;

                    return held.Count >= MaxHeldEvents || DateTime.UtcNow - holdingSince > HoldLifetime;
                }
            }
        }

        public bool Hold(EditEvent editEvent)
        {
            if (!IsProjectEdit(editEvent)) return false;

            lock (held)
            {
                if (holdingSyncId == Guid.Empty || held.Count >= MaxHeldEvents) return false;

                held.Add(editEvent);
                return true;
            }
        }

        public IReadOnlyList<EditEvent> TakeHeld()
        {
            lock (held)
            {
                holdingSyncId = Guid.Empty;
                if (held.Count == 0) return [];

                var taken = held.ToArray();
                held.Clear();
                return taken;
            }
        }

        public OnlineScenes? Receive(SyncScenesChunkEvent evt)
        {
            DropExpired();

            if (evt.TotalChunks <= 0 || evt.ChunkIndex < 0 || evt.ChunkIndex >= evt.TotalChunks) return null;

            var entry = pending.GetOrAdd(evt.SyncId, _ => new Pending { Chunks = new byte[evt.TotalChunks][] });

            if (entry.Chunks.Length != evt.TotalChunks)
            {
                pending.TryRemove(evt.SyncId, out _);
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(evt.Data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] sync chunk decode failed: {ex.Message}");
                pending.TryRemove(evt.SyncId, out _);
                return null;
            }

            lock (entry)
            {
                if (entry.Chunks[evt.ChunkIndex] != null) return null;

                entry.Chunks[evt.ChunkIndex] = bytes;
                entry.Received++;
                entry.TotalBytes += bytes.Length;

                if (entry.TotalBytes > MaxTotalBytes)
                {
                    pending.TryRemove(evt.SyncId, out _);
                    ErrorNotifier.NotifyOnce(
                        "プロジェクトが大きすぎます",
                        "相手のプロジェクトが大きすぎたため、同期を中止しました。");
                    return null;
                }

                if (entry.Received < evt.TotalChunks) return null;
            }

            pending.TryRemove(evt.SyncId, out _);

            try
            {
                var payload = new byte[entry.TotalBytes];
                var offset = 0;
                foreach (var chunk in entry.Chunks)
                {
                    if (chunk == null) return null;

                    chunk.CopyTo(payload, offset);
                    offset += chunk.Length;
                }

                return JsonSerializer.Deserialize<OnlineScenes>(payload);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] sync assemble failed: {ex.Message}");
                return null;
            }
        }

        public void Clear()
        {
            pending.Clear();

            lock (held)
            {
                held.Clear();
                holdingSyncId = Guid.Empty;
            }
        }

        private void DropExpired()
        {
            foreach (var pair in pending.ToArray())
            {
                if (pair.Value != null && DateTime.UtcNow - pair.Value.StartedAt > Lifetime)
                    pending.TryRemove(pair.Key, out _);
            }
        }
    }
}
