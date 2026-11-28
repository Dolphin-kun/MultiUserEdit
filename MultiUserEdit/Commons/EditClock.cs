using System.Collections.Concurrent;
using MultiUserEdit.Commons.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MultiUserEdit.Commons
{
    internal sealed class EditClock
    {
        private const string PositionKey = "$position";
        private const string TypeKey = "$type";

        private readonly record struct Mark(long Stamp, Guid Writer)
        {
            public bool Beats(Mark other) => Stamp != other.Stamp ? Stamp > other.Stamp : string.CompareOrdinal(Writer.ToString(), other.Writer.ToString()) > 0;
        }

        private readonly ConcurrentDictionary<(Guid Item, string Property), Mark> properties = new();
        private readonly ConcurrentDictionary<Guid, Mark> items = new();
        private long last;

        public static long ToStamp(DateTime at) => new DateTimeOffset(at.ToUniversalTime()).ToUnixTimeMilliseconds();

        public long Next()
        {
            while (true)
            {
                var seen = Volatile.Read(ref last);
                var next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seen + 1);
                if (Interlocked.CompareExchange(ref last, next, seen) == seen) return next;
            }
        }

        private void Observe(long stamp)
        {
            var seen = Volatile.Read(ref last);
            while (stamp > seen)
            {
                var found = Interlocked.CompareExchange(ref last, stamp, seen);
                if (found == seen) return;
                seen = found;
            }
        }

        public EditEvent Stamp(EditEvent edit)
        {
            var stamped = edit with { Stamp = Next() };
            Note(stamped, stamped.Stamp);
            return stamped;
        }

        public EditEvent? Accept(EditEvent edit)
        {
            var stamp = edit.Stamp > 0 ? edit.Stamp : Next();
            Observe(stamp);
            var kept = edit is ItemRemovedEvent ? edit : Filter(edit, stamp);
            if (kept != null) Note(kept, stamp);
            return kept;
        }

        public void Note(EditEvent edit, long stamp)
        {
            var mark = new Mark(stamp, edit.ExecutorId);
            switch (edit)
            {
                case ItemUpdatedEvent updated:
                    foreach (var name in PropertyNames(updated.ItemJson)) Raise(properties, (updated.ItemId, name), mark);
                    Raise(items, updated.ItemId, mark);
                    break;
                case ItemMovedEvent moved:
                    Raise(properties, (moved.ItemId, PositionKey), mark);
                    Raise(items, moved.ItemId, mark);
                    break;
                case ItemAddedEvent added:
                    Raise(items, added.ItemId, mark);
                    break;
                case ItemRemovedEvent removed:
                    Raise(items, removed.ItemId, mark);
                    break;
            }
        }

        public EditEvent? Filter(EditEvent edit, long stamp)
        {
            var mark = new Mark(stamp, edit.ExecutorId);
            switch (edit)
            {
                case ItemUpdatedEvent updated:
                    {
                        JObject patch;
                        try
                        {
                            patch = JObject.Parse(updated.ItemJson);
                        }
                        catch (JsonException)
                        {
                            return null;
                        }
                        var stale = patch.Properties()
                            .Where(p => p.Name != TypeKey && IsNewer(properties, (updated.ItemId, p.Name), mark))
                            .ToList();
                        if (stale.Count == 0) return edit;
                        foreach (var property in stale) property.Remove();
                        return patch.Properties().Any(p => p.Name != TypeKey)
                            ? updated with { ItemJson = patch.ToString(Formatting.None) }
                            : null;
                    }
                case ItemMovedEvent moved:
                    return IsNewer(properties, (moved.ItemId, PositionKey), mark) ? null : edit;
                case ItemRemovedEvent removed:
                    return IsNewer(items, removed.ItemId, mark) ? null : edit;
                default:
                    return edit;
            }
        }

        private static IEnumerable<string> PropertyNames(string json)
        {
            try
            {
                return [.. JObject.Parse(json).Properties().Select(p => p.Name).Where(name => name != TypeKey)];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static void Raise<TKey>(ConcurrentDictionary<TKey, Mark> map, TKey key, Mark mark) where TKey : notnull =>
            map.AddOrUpdate(key, mark, (_, old) => old.Beats(mark) ? old : mark);

        private static bool IsNewer<TKey>(ConcurrentDictionary<TKey, Mark> map, TKey key, Mark mark) where TKey : notnull =>
            map.TryGetValue(key, out var existing) && existing.Beats(mark);
    }
}
