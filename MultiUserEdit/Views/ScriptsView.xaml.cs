using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiUserEdit.Views
{
    public partial class ScriptsView : UserControl
    {
        public ScriptsView()
        {
            InitializeComponent();
        }

        private void OnSerifKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || sender is not TextBox box) return;

            e.Handled = true;
            var index = Rows.Items.IndexOf(box.DataContext);
            Rows.UpdateLayout();
            if (Rows.ItemContainerGenerator.ContainerFromIndex(index + 1) is DependencyObject next && FindTextBox(next) is { } target)
            {
                target.Focus();
                target.CaretIndex = target.Text.Length;
            }
        }

        private static TextBox? FindTextBox(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBox box) return box;
                if (FindTextBox(child) is { } found) return found;
            }
            return null;
        }
    }
}
