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

        private string userName = "ユーザー";
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

        // 接続ごとに発行されるUserIdと異なり、インストール単位で変わらないID。
        // プロジェクトに保存する合計参加時間を同一人物として積算するために使う。
        private string profileId = string.Empty;
        public string ProfileId
        {
            get => profileId;
            set => Set(ref profileId, value);
        }

        private bool confirmBeforeFileSend = true;
        public bool ConfirmBeforeFileSend
        {
            get => confirmBeforeFileSend;
            set => Set(ref confirmBeforeFileSend, value);
        }

        private string allowedExtensions = Commons.Models.FileExtensionCatalog.DefaultCsv;
        public string AllowedExtensions
        {
            get => allowedExtensions;
            set => Set(ref allowedExtensions, value);
        }

        public bool IsExtensionAllowed(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var ext = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(ext)) return false;

            var allowedList = AllowedExtensions
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x.StartsWith(".") ? x : "." + x);

            return allowedList.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        public override void Initialize()
        {
            if (string.IsNullOrEmpty(UserName))
                UserName = "ユーザー";

            if (!Guid.TryParse(ProfileId, out _))
                ProfileId = Guid.NewGuid().ToString();

            if (string.IsNullOrWhiteSpace(AllowedExtensions))
                AllowedExtensions = Commons.Models.FileExtensionCatalog.DefaultCsv;
        }
    }
}
