using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons.Models
{
    /// <summary>共有可能な拡張子の1行分。チェックの変更は <see cref="Changed"/> で通知する。</summary>
    internal class ExtensionOption : Bindable
    {
        public string Extension { get; }
        public string GroupName { get; }

        /// <summary>一覧に無い、ユーザーが自分で追加した拡張子か（削除できる）</summary>
        public bool IsCustom => GroupName == FileExtensionCatalog.CustomGroupName;

        public event Action? Changed;

        private bool isEnabled;
        public bool IsEnabled
        {
            get => isEnabled;
            set
            {
                if (Set(ref isEnabled, value)) Changed?.Invoke();
            }
        }

        public ExtensionOption(string extension, bool isEnabled)
        {
            Extension = extension;
            GroupName = FileExtensionCatalog.GetGroupName(extension);
            this.isEnabled = isEnabled;
        }

        /// <summary>通知を出さずに状態だけ差し替える（一括操作で保存が何度も走るのを防ぐ）</summary>
        public void SetEnabledSilently(bool value)
        {
            if (isEnabled == value) return;
            isEnabled = value;
            OnPropertyChanged(nameof(IsEnabled));
        }
    }
}
