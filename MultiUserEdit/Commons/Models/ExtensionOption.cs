using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons.Models
{
    internal class ExtensionOption(string extension, bool isEnabled) : Bindable
    {
        public string Extension { get; } = extension;
        public FileGroupKind GroupKind { get; } = FileExtensionCatalog.GetKind(extension);

        public string GroupName => FileExtensionCatalog.NameOf(GroupKind);

        public bool IsCustom => GroupKind == FileGroupKind.Custom;

        public event Action? Changed;

        private bool isEnabled = isEnabled;
        public bool IsEnabled
        {
            get => isEnabled;
            set
            {
                if (Set(ref isEnabled, value)) Changed?.Invoke();
            }
        }

        public void SetEnabledSilently(bool value)
        {
            if (isEnabled == value) return;
            isEnabled = value;
            OnPropertyChanged(nameof(IsEnabled));
        }
    }
}
