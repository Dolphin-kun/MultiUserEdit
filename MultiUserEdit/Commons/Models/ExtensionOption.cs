using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons.Models
{
    internal class ExtensionOption(string extension, bool isEnabled) : Bindable
    {
        public string Extension { get; } = extension;
        public string GroupName { get; } = FileExtensionCatalog.GetGroupName(extension);

        public bool IsCustom => GroupName == FileExtensionCatalog.CustomGroupName;

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
