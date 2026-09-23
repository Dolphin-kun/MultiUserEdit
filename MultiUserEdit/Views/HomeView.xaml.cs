using MultiUserEdit.Commons.Models;
using MultiUserEdit.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace MultiUserEdit.Views
{
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();

            UpdateNotice.DataContext = Commons.UpdateChecker.Instance;
            Commons.UpdateChecker.Instance.EnsureChecked();
        }

        private void OnInputPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            Behaviors.MouseWheelRedirector.Redirect(sender, e);
        }

        private void OnInputRoomIdPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is MultiUserEditViewModel viewModel)
            {
                viewModel.InputRoomId = InputRoomIdPasswordBox.Password;
            }
        }

        private void OnJoinRoomClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(() => InputRoomIdPasswordBox.Password = string.Empty,
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnParticipantMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not Participant participant) return;
            if (DataContext is not MultiUserEditViewModel viewModel) return;

            var isSelf = participant.UserId == viewModel.LocalUserId;
            var canManage = viewModel.IsHost && participant.Role == UserRole.Guest;

            var dialog = new UserProfileDialog(participant, isSelf, canManage,
                () => viewModel.GetTotalParticipationSeconds(participant))
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() != true) return;

            if (dialog.KickRequested)
            {
                viewModel.KickUserCommand?.Execute(participant.UserId);
                return;
            }

            if (dialog.SyncFromRequested)
            {
                viewModel.SyncFrom(participant.UserId);
                return;
            }

            if (isSelf)
            {
                viewModel.UserName = dialog.ResultUserName;
                viewModel.UserDescription = dialog.ResultDescription;
            }

            if (canManage)
            {
                viewModel.UpdateUserPermission(participant.UserId, dialog.ResultPermission);
            }
        }
    }
}
