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

                // YMM4は多重起動できず、既に起動中の状態でYMM4.exeに引数付きで再起動しても無視されるだけで
                // 「新しいウィンドウを開く」動作にはならない。一方、引数無しで起動すると既存インスタンスがある場合に
                // 新しいウィンドウを開いてくれるため、ディープリンクの内容は一時ファイル経由で渡し、
                // YMM4自体はPowerShell経由で無引数起動する。
                var psCommand = $"powershell.exe -WindowStyle Hidden -Command \"[System.IO.File]::WriteAllText([System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'deeplink.txt'), '%1'); Start-Process '{processPath}'\"";
                commandKey?.SetValue("", psCommand);

                Debug.WriteLine($"[MultiUserEdit] Protocol {UriScheme}:// registered to {processPath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Protocol registration failed: {ex.Message}");
                return false;
            }
        }

        public static bool UnregisterCustomProtocol()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@$"Software\Classes\{UriScheme}", false);
                Debug.WriteLine($"[MultiUserEdit] Protocol {UriScheme}:// unregistered");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Protocol unregistration failed: {ex.Message}");
                return false;
            }
        }
    }
}
