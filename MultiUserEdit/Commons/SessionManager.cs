using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Project;

namespace MultiUserEdit.Commons
{
    public static class SessionManager
    {
        private static readonly ConditionalWeakTable<Scenes, CollaborationSession> sessions = new();

        public static CollaborationSession GetOrCreate(Scenes scenes)
        {
            return sessions.GetValue(scenes, s => new CollaborationSession(s));
        }

        public static CollaborationSession? Get(Scenes scenes)
        {
            sessions.TryGetValue(scenes, out var session);
            return session;
        }
    }
}
