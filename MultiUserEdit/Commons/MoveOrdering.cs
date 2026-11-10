using System.Collections.Concurrent;

namespace MultiUserEdit.Commons
{
    internal static class MoveOrdering
    {
        private static readonly ConcurrentDictionary<(Guid Sender, Guid Item), long> latest = new();

        public static bool IsStale(Guid sender, Guid item, long sequence)
        {
            if (sequence <= 0) return false;

            var stale = false;
            latest.AddOrUpdate((sender, item), sequence, (_, previous) =>
            {
                if (sequence > previous) return sequence;

                stale = true;
                return previous;
            });

            return stale;
        }

        public static void Clear() => latest.Clear();
    }
}
