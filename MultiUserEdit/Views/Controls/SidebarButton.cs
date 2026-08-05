using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiUserEdit.Views.Controls
{
    public class SidebarButton : RadioButton
    {
        static SidebarButton()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(SidebarButton), new FrameworkPropertyMetadata(typeof(SidebarButton)));
        }

        public SidebarButton()
        {
            Cursor = Cursors.Hand;
        }

        public static readonly DependencyProperty IconDataProperty =
            DependencyProperty.Register(nameof(IconData), typeof(Geometry), typeof(SidebarButton), new PropertyMetadata(null));

        public Geometry IconData
        {
            get => (Geometry)GetValue(IconDataProperty);
            set => SetValue(IconDataProperty, value);
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(SidebarButton), new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }
    }
}
