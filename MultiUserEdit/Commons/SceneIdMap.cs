namespace MultiUserEdit.Commons
{
    internal static class SceneIdMap
    {
        private static readonly Dictionary<Guid, Guid> remoteToLocal = [];

        public static void Clear()
        {
            lock (remoteToLocal) remoteToLocal.Clear();
        }

        public static void Remember(Guid remoteId, Guid localId)
        {
            if (remoteId == Guid.Empty || localId == Guid.Empty || remoteId == localId) return;

            lock (remoteToLocal) remoteToLocal[remoteId] = localId;
        }

        public static Guid ToLocal(Guid remoteId)
        {
            if (remoteId == Guid.Empty) return remoteId;

            lock (remoteToLocal) return remoteToLocal.TryGetValue(remoteId, out var localId) ? localId : remoteId;
        }
    }
}
