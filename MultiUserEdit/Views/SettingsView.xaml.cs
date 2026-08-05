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
    }
}
