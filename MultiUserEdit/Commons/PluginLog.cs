using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons
{
    internal static class PluginLog
    {
        public static void Error(string message, Exception? exception = null)
        {
            try
            {
                if (exception == null) Log.Default.Write($"[MultiUserEdit] {message}");
                else Log.Default.Write($"[MultiUserEdit] {message}", exception);
            }
            catch
            {
            }
        }
    }
}
