using MultiUserEdit.Commons;
using MultiUserEdit.ViewModels;
using MultiUserEdit.Views.Controls;
using System.Windows;
using System.Windows.Controls;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.Views;

namespace MultiUserEdit.Views
{
    public partial class MultiUserEditView : UserControl
    {
        private HomeView? homeView;
        private RulesView? rulesView;
        private FilesView? filesView;
        private FilesViewModel? filesViewModel;

        private SettingsPages.ProfileSettingsView? profileSettingsView;
        private SettingsPages.FileSettingsView? fileSettingsView;
        private SettingsPages.LinkSettingsView? linkSettingsView;
        private SettingsPages.AboutSettingsView? aboutSettingsView;

        private object? lastMainContent;
        private SidebarButton? lastMainNavButton;

        public MultiUserEditView()
        {
            InitializeComponent();

            DataContextChanged += MultiUserEditView_DataContextChanged;
            Loaded += MultiUserEditView_Loaded;
        }

        private void MultiUserEditView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            homeView = new HomeView { DataContext = DataContext };
            rulesView = new RulesView { DataContext = DataContext };

            profileSettingsView = new SettingsPages.ProfileSettingsView { DataContext = DataContext };
            fileSettingsView = new SettingsPages.FileSettingsView { DataContext = DataContext };
            linkSettingsView = new SettingsPages.LinkSettingsView { DataContext = DataContext };
            aboutSettingsView = new SettingsPages.AboutSettingsView { DataContext = DataContext };

            filesViewModel = new FilesViewModel(DataContext as MultiUserEditViewModel);
            filesView = new FilesView { DataContext = filesViewModel };

            MainContentControl.Content = homeView;
        }

        private void MultiUserEditView_Loaded(object sender, RoutedEventArgs e)
        {
            SetupPreviewSeekAction();
        }

        private void SetupPreviewSeekAction()
        {
            if (DataContext is not MultiUserEditViewModel viewModel) return;

            var parentWindow = Window.GetWindow(this);
            viewModel.AdornerManager?.AttachAdorner(viewModel, parentWindow);

            var previewVm = (VisualTreeHelperExtensions.FindVisualChild<PreviewView>(parentWindow) as FrameworkElement)?.DataContext;
            var isPlayingProp = previewVm?.GetType().GetProperty("IsPlaying");

            bool GetCurrentPlaying()
            {
                try
                {
                    if (previewVm != null && isPlayingProp?.GetValue(previewVm) is bool b) return b;
                }
                catch { }
                return false;
            }

            viewModel.SetPreviewSeekAction(
                (frame, shouldPlay) =>
                {
                    var seekCmd = CommandSettings.Default[CommandType.Seek];
                    var playPauseCmd = CommandSettings.Default[CommandType.PlayPause];

                    var currentlyPlaying = GetCurrentPlaying();

                    if (!shouldPlay)
                    {
                        if (currentlyPlaying && playPauseCmd != null && playPauseCmd.CanExecute(null, parentWindow))
                        {
                            playPauseCmd.Execute(null, parentWindow);
                        }

                        if (seekCmd != null && seekCmd.CanExecute(frame, parentWindow))
                        {
                            seekCmd.Execute(frame, parentWindow);
                        }
                    }
                    else
                    {
                        if (!currentlyPlaying)
                        {
                            if (seekCmd != null && seekCmd.CanExecute(frame, parentWindow))
                            {
                                seekCmd.Execute(frame, parentWindow);
                            }

                            if (playPauseCmd != null && playPauseCmd.CanExecute(null, parentWindow))
                            {
                                playPauseCmd.Execute(null, parentWindow);
                            }
                        }
                    }

                    return Task.CompletedTask;
                },
                GetCurrentPlaying
            );
        }

        private void OnNavHomeClick(object sender, RoutedEventArgs e)
        {
            if (homeView != null)
                MainContentControl.Content = homeView;
        }

        private void OnNavRulesClick(object sender, RoutedEventArgs e)
        {
            if (rulesView != null)
                MainContentControl.Content = rulesView;
        }

        private void OnNavFilesClick(object sender, RoutedEventArgs e)
        {
            if (filesView != null)
            {
                filesViewModel?.ExecuteRefresh(null);
                MainContentControl.Content = filesView;
            }
        }

        private void OnNavSettingsClick(object sender, RoutedEventArgs e)
        {
            lastMainContent = MainContentControl.Content;
            lastMainNavButton = new[] { HomeNavButton, RulesNavButton, FilesNavButton }
                .FirstOrDefault(button => button.IsChecked == true);

            MainNavPanel.Visibility = Visibility.Collapsed;
            MainNavFooter.Visibility = Visibility.Collapsed;
            SettingsNavPanel.Visibility = Visibility.Visible;
            SettingsNavFooter.Visibility = Visibility.Visible;

            ProfileSettingsNavButton.IsChecked = true;
            MainContentControl.Content = profileSettingsView;
        }

        private void OnNavSettingsBackClick(object sender, RoutedEventArgs e)
        {
            SettingsNavPanel.Visibility = Visibility.Collapsed;
            SettingsNavFooter.Visibility = Visibility.Collapsed;
            MainNavPanel.Visibility = Visibility.Visible;
            MainNavFooter.Visibility = Visibility.Visible;

            SettingsBackButton.IsChecked = false;

            if (lastMainNavButton != null) lastMainNavButton.IsChecked = true;
            else HomeNavButton.IsChecked = true;

            MainContentControl.Content = lastMainContent ?? homeView;
        }

        private void OnNavProfileSettingsClick(object sender, RoutedEventArgs e)
        {
            MainContentControl.Content = profileSettingsView;
        }

        private void OnNavFileSettingsClick(object sender, RoutedEventArgs e)
        {
            MainContentControl.Content = fileSettingsView;
        }

        private void OnNavLinkSettingsClick(object sender, RoutedEventArgs e)
        {
            MainContentControl.Content = linkSettingsView;
        }

        private void OnNavAboutSettingsClick(object sender, RoutedEventArgs e)
        {
            MainContentControl.Content = aboutSettingsView;
        }
    }
}
