using Microsoft.Win32;
using System.Diagnostics;

namespace MultiUserEdit.Commons
{
    internal static class ProtocolRegister
    {
        public const string UriScheme = "ymm4-multi-user-edit";
        private const string FriendlyName = "ゆっくりMovieMaker";

        public static bool IsRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@$"Software\Classes\{UriScheme}");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool RegisterCustomProtocol()
        {
            try
            {
                var processPath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(processPath)) return false;

                using var key = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{UriScheme}");
                if (key == null) return false;

                key.SetValue("", $"URL:{FriendlyName}");
                key.SetValue("URL Protocol", "");
                key.SetValue("FriendlyTypeName", FriendlyName);

                using var defaultIconKey = key.CreateSubKey("DefaultIcon");
                defaultIconKey?.SetValue("", $"\"{processPath}\",0");

                using var commandKey = key.CreateSubKey(@"shell\open\command");

                commandKey?.SetValue("", $"\"{processPath}\" \"%1\"");

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool UnregisterCustomProtocol()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@$"Software\Classes\{UriScheme}", false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
