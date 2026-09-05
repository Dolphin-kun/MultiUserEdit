using MultiUserEdit.Commons;
using System.Windows.Controls;
using System.Windows.Input;

namespace MultiUserEdit.Views.SettingsPages
{
    public partial class ProfileSettingsView : UserControl
    {
        public ProfileSettingsView()
        {
            InitializeComponent();

            DescriptionBox.MaxLength = ProfileText.MaxDescriptionLength;
            DescriptionHint.Text = ProfileText.DescriptionHint;
        }

        // 入力欄がホイールを吸ってしまい、ページ全体のスクロールが止まるのを防ぐ
        private void OnInputPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            Behaviors.MouseWheelRedirector.Redirect(sender, e);
        }
    }
}
