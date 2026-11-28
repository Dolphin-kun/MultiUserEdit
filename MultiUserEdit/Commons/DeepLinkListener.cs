using System.Reflection;
using System.Windows;

namespace MultiUserEdit.Commons
{
    internal static class DeepLinkListener
    {
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan StartupForwardWindow = TimeSpan.FromMinutes(2);
        private static readonly DateTime startedAt = DateTime.UtcNow;
        private static string? pendingRoomId;
        private static string? startupRoomId;
        private static string? lastRoomId;
        private static DateTime lastAt;
        private static int started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;

            startupRoomId = Environment.GetCommandLineArgs().Select(ParseRoomId).FirstOrDefault(id => id != null);
            pendingRoomId = startupRoomId;
            if (!Hook()) Application.Current?.Dispatcher.BeginInvoke(() => Hook(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        public static void TakePending(Action<string> joinRoom)
        {
            if (Interlocked.Exchange(ref pendingRoomId, null) is { } pending) joinRoom(pending);
        }

        private static bool Hook()
        {
            try
            {
                var app = Application.Current;
                var server = app?.GetType().GetField("server", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(app);
                var openFile = server?.GetType().GetEvent("OpenFileCommandRequested");
                if (server == null || openFile == null) return false;
                openFile.AddEventHandler(server, new EventHandler<string>((_, path) => app?.Dispatcher.InvokeAsync(() => Accept(path))));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Accept(string? text)
        {
            if (ParseRoomId(text) is not { } roomId) return;
            if (roomId == startupRoomId && DateTime.UtcNow - startedAt < StartupForwardWindow) return;
            if (roomId == lastRoomId && DateTime.UtcNow - lastAt < DuplicateWindow) return;
            lastRoomId = roomId;
            lastAt = DateTime.UtcNow;

            pendingRoomId = roomId;
            OpenNewWindow();
        }

        private static void OpenNewWindow()
        {
            try
            {
                var app = Application.Current;
                app?.GetType().GetMethod("Server_CreateNewWindowCommandRequested", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(app, [null, EventArgs.Empty]);
            }
            catch
            {
            }
        }

        internal static string? ParseRoomId(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            var isLink = text.StartsWith($"{ProtocolRegister.UriScheme}://", StringComparison.OrdinalIgnoreCase) || text.StartsWith('/');
            return isLink ? RoomIdParser.Parse(text) : null;
        }
    }
}
