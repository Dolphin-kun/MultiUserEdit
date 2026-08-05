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
        }

        private void OnEditParticipantPermissionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Participant participant)
            {
                var viewModel = DataContext as MultiUserEditViewModel;
                if (viewModel == null) return;

                var dialog = new UserPermissionDialog(participant.UserName, participant.Permission)
                {
                    Owner = Window.GetWindow(this)
                };

                if (dialog.ShowDialog() == true)
                {
                    viewModel.UpdateUserPermission(participant.UserId, dialog.ResultPermission);
                }
            }
        }
    }
}
