using MultiUserEdit.Commons;
using System.Windows.Controls;

namespace MultiUserEdit.Views.SettingsPages
{
    public partial class AboutSettingsView : UserControl
    {
        public AboutSettingsView()
        {
            InitializeComponent();

            VersionCard.DataContext = UpdateChecker.Instance;
            UpdateChecker.Instance.EnsureChecked();
        }
    }
}
