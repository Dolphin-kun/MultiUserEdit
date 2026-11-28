using System.Collections.Concurrent;

namespace MultiUserEdit.Commons
{
    internal static class RenderReadiness
    {
        private static readonly ConcurrentDictionary<Guid, int> unrenderable = new();

        public static void MarkMissing(Guid itemId, int timelineIndex) => unrenderable[itemId] = timelineIndex;

        public static void MarkPresent(Guid itemId) => unrenderable.TryRemove(itemId, out _);

        public static void Clear() => unrenderable.Clear();

        public static int MissingIn(int timelineIndex) => unrenderable.Values.Count(index => index == timelineIndex);
    }
}
