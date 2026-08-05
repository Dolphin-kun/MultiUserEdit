using MultiUserEdit.Commons.Models;
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

        public override void Initialize()
        {
            if (string.IsNullOrEmpty(UserName))
                UserName = "ユーザー";
        }
    }
}
