using MultiUserEdit.Views.Converters;
using System.Windows.Controls;

namespace MultiUserEdit.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            Resources.Add("EnumToBoolConverter", new EnumToBoolConverter());
            InitializeComponent();
        }

        // 入力欄がホイールを吸ってしまい、設定画面全体のスクロールが止まるのを防ぐ
        private void OnInputPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            Behaviors.MouseWheelRedirector.Redirect(sender, e);
        }
    }
}
