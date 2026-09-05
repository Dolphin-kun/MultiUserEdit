using MultiUserEdit.Commons;
using System.Windows.Controls;

namespace MultiUserEdit.Views.SettingsPages
{
    public partial class AboutSettingsView : UserControl
    {
        public AboutSettingsView()
        {
            InitializeComponent();

            // バージョン欄だけはUpdateCheckerを見る（ページ全体のDataContextはViewModelのまま）
            VersionCard.DataContext = UpdateChecker.Instance;
            UpdateChecker.Instance.EnsureChecked();
        }
    }
}
