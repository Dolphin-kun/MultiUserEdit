using MultiUserEdit.Commons;
using MultiUserEdit.ViewModels;
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
        private SettingsView? settingsView;
        private FilesView? filesView;
        private FilesViewModel? filesViewModel;

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
            settingsView = new SettingsView { DataContext = DataContext };

            filesViewModel = new FilesViewModel();
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

            // PreviewViewModel は非公開型で IsPlaying を公開する API が無いためリフレクション参照が必要。
            // ただしビジュアルツリー探索とGetProperty解決は毎フレーム行うと非常に重いので、初回のみ実施してキャッシュする。
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

        private void OnNavSettingsClick(object sender, RoutedEventArgs e)
        {
            if (settingsView != null)
                MainContentControl.Content = settingsView;
        }

        private void OnNavFilesClick(object sender, RoutedEventArgs e)
        {
            if (filesView != null)
            {
                filesViewModel?.ExecuteRefresh(null);
                MainContentControl.Content = filesView;
            }
        }
    }
}
        