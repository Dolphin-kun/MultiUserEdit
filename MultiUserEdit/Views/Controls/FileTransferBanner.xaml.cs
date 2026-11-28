using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using YukkuriMovieMaker.Resources.Icons;

namespace MultiUserEdit.Views.Controls
{
    public partial class FileTransferBanner : UserControl
    {
        private bool isExpanded = true;
        private MouseButtonEventHandler? externalClickHandler;
        private Window? hookedWindow;

        public FileTransferBanner()
        {
            InitializeComponent();
            SetExpanded(isExpanded);
            RootGrid.IsVisibleChanged += (s, e) =>
            {
                if (RootGrid.IsVisible) SetExpanded(true);
            };
            Loaded += FileTransferBanner_Loaded;
            Unloaded += FileTransferBanner_Unloaded;
        }

        private void FileTransferBanner_Loaded(object sender, RoutedEventArgs e)
        {
            externalClickHandler = (s, args) =>
            {
                if (!isExpanded) return;

                var pos = args.GetPosition(this);
                if (pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight)
                {
                    SetExpanded(false);
                }
            };

            hookedWindow = Window.GetWindow(this);
            hookedWindow?.PreviewMouseDown += externalClickHandler;
        }

        private void FileTransferBanner_Unloaded(object sender, RoutedEventArgs e)
        {
            if (externalClickHandler != null) hookedWindow?.PreviewMouseDown -= externalClickHandler;
            hookedWindow = null;
        }

        private void OnToggleClick(object sender, RoutedEventArgs e)
        {
            SetExpanded(!isExpanded);
        }

        private void SetExpanded(bool expanded)
        {
            isExpanded = expanded;
            DetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            ArrowPath.SetResourceReference(System.Windows.Shapes.Path.DataProperty, expanded ? GeometryKeys.ChevronUp : GeometryKeys.ChevronDown);
        }
    }
}
