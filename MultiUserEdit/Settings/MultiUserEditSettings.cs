using MultiUserEdit.Commons.Models;
using System.IO;
using YukkuriMovieMaker.Plugin;

namespace MultiUserEdit.Settings
{
    public class MultiUserEditSettings : SettingsBase<MultiUserEditSettings>
    {
        public override SettingsCategory Category => SettingsCategory.Tool;
        public override string Name => "MultiUserEdit";

        public override bool HasSettingView => false;
        public override object? SettingView => null;

        private FileStorageMode storageMode = FileStorageMode.TemporarySession;
        public FileStorageMode StorageMode
        {
            get => storageMode;
            set => Set(ref storageMode, value);
        }

        private string userName = Texts.DefaultUserName;
        public string UserName
        {
            get => userName;
            set => Set(ref userName, value);
        }

        private string userDescription = string.Empty;
        public string UserDescription
        {
            get => userDescription;
            set => Set(ref userDescription, value);
        }

        private string profileId = string.Empty;
        public string ProfileId
        {
            get => profileId;
            set => Set(ref profileId, value);
        }

        private bool useDirectTransfer = true;
        public bool UseDirectTransfer
        {
            get => useDirectTransfer;
            set => Set(ref useDirectTransfer, value);
        }

        private bool directTransferAsked;
        public bool DirectTransferAsked
        {
            get => directTransferAsked;
            set => Set(ref directTransferAsked, value);
        }

        private bool confirmBeforeFileSend = true;
        public bool ConfirmBeforeFileSend
        {
            get => confirmBeforeFileSend;
            set => Set(ref confirmBeforeFileSend, value);
        }

        private string customVoiceDirectories = string.Empty;
        public string CustomVoiceDirectories
        {
            get => customVoiceDirectories;
            set => Set(ref customVoiceDirectories, value);
        }

        private bool showOperatorNames = true;
        public bool ShowOperatorNames
        {
            get => showOperatorNames;
            set => Set(ref showOperatorNames, value);
        }

        private double sidebarWidth = 124;
        public double SidebarWidth
        {
            get => sidebarWidth;
            set => Set(ref sidebarWidth, value);
        }

        private bool showActiveTransfers = true;
        public bool ShowActiveTransfers
        {
            get => showActiveTransfers;
            set => Set(ref showActiveTransfers, value);
        }

        private bool confirmLargeFileReceive = true;
        public bool ConfirmLargeFileReceive
        {
            get => confirmLargeFileReceive;
            set => Set(ref confirmLargeFileReceive, value);
        }

        private int largeFileConfirmMegaBytes = 100;
        public int LargeFileConfirmMegaBytes
        {
            get => largeFileConfirmMegaBytes;
            set => Set(ref largeFileConfirmMegaBytes, Math.Clamp(value, 1, 100000));
        }

        private string allowedExtensions = Commons.Models.FileExtensionCatalog.DefaultCsv;
        public string AllowedExtensions
        {
            get => allowedExtensions;
            set => Set(ref allowedExtensions, value);
        }

        private string keptRoomKeys = string.Empty;
        public string KeptRoomKeys
        {
            get => keptRoomKeys;
            set => Set(ref keptRoomKeys, value);
        }

        private Dictionary<string, string> ParseKeptRoomKeys() =>
            KeptRoomKeys
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

        public string? GetKeptRoomKey(string roomId) =>
            ParseKeptRoomKeys().TryGetValue(roomId, out var key) ? key : null;

        public void SetKeptRoomKey(string roomId, string? hostKey)
        {
            var keys = ParseKeptRoomKeys();
            if (string.IsNullOrEmpty(hostKey)) keys.Remove(roomId);
            else keys[roomId] = hostKey;

            KeptRoomKeys = string.Join("\n", keys.Select(pair => $"{pair.Key}={pair.Value}"));
        }

        public bool IsExtensionAllowed(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var ext = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(ext)) return false;

            var allowedList = AllowedExtensions
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x.StartsWith('.') ? x : "." + x);

            return allowedList.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        public override void Initialize()
        {
            if (string.IsNullOrEmpty(UserName))
                UserName = Texts.DefaultUserName;

            if (!Guid.TryParse(ProfileId, out _))
                ProfileId = Guid.NewGuid().ToString();

            if (string.IsNullOrWhiteSpace(AllowedExtensions))
                AllowedExtensions = Commons.Models.FileExtensionCatalog.DefaultCsv;
        }
    }
}
