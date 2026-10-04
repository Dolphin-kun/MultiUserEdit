using MultiUserEdit.Commons.Models;
using MultiUserEdit.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace MultiUserEdit.Views
{
    public partial class RulesView : UserControl
    {
        private MultiUserEditViewModel? viewModel;

        public RulesView()
        {
            InitializeComponent();
            DataContextChanged += RulesView_DataContextChanged;
        }

        private void RulesView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            viewModel?.PropertyChanged -= ViewModel_PropertyChanged;

            viewModel = DataContext as MultiUserEditViewModel;
            if (viewModel != null)
            {
                viewModel.PropertyChanged += ViewModel_PropertyChanged;
                UpdateUiState();
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MultiUserEditViewModel.IsHost) ||
                e.PropertyName == nameof(MultiUserEditViewModel.CurrentUserPermission))
            {
                UpdateUiState();
            }
        }

        private void UpdateUiState()
        {
            if (viewModel == null) return;

            GuestPanel.Visibility = viewModel.IsHost ? Visibility.Collapsed : Visibility.Visible;

            if (viewModel.IsHost)
            {
                LoadPermissionToUi(viewModel.GlobalDefaultPermission);
            }
            else
            {
                LoadGuestPermissionStatus(viewModel.CurrentUserPermission);
            }
        }

        private void LoadPermissionToUi(UserPermission perm)
        {
            GlobalFullRadio.IsChecked = perm.Level == PermissionLevel.Full;
            GlobalStandardRadio.IsChecked = perm.Level == PermissionLevel.Standard;
            GlobalReadOnlyRadio.IsChecked = perm.Level == PermissionLevel.ReadOnly;
            GlobalLiveMirrorRadio.IsChecked = perm.Level == PermissionLevel.LiveMirror;

            ApplyChecks(perm);
        }

        private void ApplyChecks(UserPermission perm)
        {
            GlobalAddItemsCheck.IsChecked = perm.CanAddItems;
            GlobalMoveItemsCheck.IsChecked = perm.CanMoveItems;
            GlobalDeleteItemsCheck.IsChecked = perm.CanDeleteItems;
            GlobalDeleteOthersCheck.IsChecked = perm.CanDeleteOthersItems;
            GlobalEditPropsCheck.IsChecked = perm.CanEditProperties;
            GlobalAddScenesCheck.IsChecked = perm.CanAddScenes;
            GlobalDeleteScenesCheck.IsChecked = perm.CanDeleteScenes;
            GlobalShareFilesCheck.IsChecked = perm.CanShareFiles;
            GlobalSyncSeekCheck.IsChecked = perm.CanSyncSeekPosition;
        }

        private static string StateText(bool allowed) => allowed ? "許可" : "制限中";

        private void LoadGuestPermissionStatus(UserPermission perm)
        {
            GuestLevelText.Text = perm.Level switch
            {
                PermissionLevel.Full => "フル編集",
                PermissionLevel.Standard => "標準編集",
                PermissionLevel.ReadOnly => "閲覧のみ",
                PermissionLevel.LiveMirror => "ライブミラーリング",
                _ => "不明"
            };

            GuestAddText.Text = StateText(perm.CanAddItems);
            GuestMoveText.Text = StateText(perm.CanMoveItems);
            GuestDeleteText.Text = StateText(perm.CanDeleteItems);
            GuestDeleteOthersText.Text = StateText(perm.CanDeleteItems && perm.CanDeleteOthersItems);
            GuestPropsText.Text = StateText(perm.CanEditProperties);
            GuestAddSceneText.Text = StateText(perm.CanAddScenes);
            GuestDeleteSceneText.Text = StateText(perm.CanDeleteScenes);
            GuestShareFilesText.Text = StateText(perm.CanShareFiles);
            GuestSyncSeekText.Text = perm.CanSyncSeekPosition ? "オン" : "オフ";
        }

        private PermissionLevel SelectedLevel()
        {
            if (GlobalFullRadio.IsChecked == true) return PermissionLevel.Full;
            if (GlobalStandardRadio.IsChecked == true) return PermissionLevel.Standard;
            if (GlobalReadOnlyRadio.IsChecked == true) return PermissionLevel.ReadOnly;
            return PermissionLevel.LiveMirror;
        }

        private void OnGlobalPresetChecked(object sender, RoutedEventArgs e)
        {
            if (GlobalAddItemsCheck == null) return;

            ApplyChecks(UserPermission.CreateFromLevel(SelectedLevel()));
        }

        private void OnApplyGlobalRulesClick(object sender, RoutedEventArgs e)
        {
            if (viewModel == null) return;

            var newPerm = new UserPermission
            {
                Level = SelectedLevel(),
                CanAddItems = GlobalAddItemsCheck.IsChecked == true,
                CanMoveItems = GlobalMoveItemsCheck.IsChecked == true,
                CanDeleteItems = GlobalDeleteItemsCheck.IsChecked == true,
                CanDeleteOthersItems = GlobalDeleteOthersCheck.IsChecked == true,
                CanEditProperties = GlobalEditPropsCheck.IsChecked == true,
                CanAddScenes = GlobalAddScenesCheck.IsChecked == true,
                CanDeleteScenes = GlobalDeleteScenesCheck.IsChecked == true,
                CanShareFiles = GlobalShareFilesCheck.IsChecked == true,
                CanSyncSeekPosition = GlobalSyncSeekCheck.IsChecked == true
            };

            viewModel.UpdateGlobalPermission(newPerm);
            MessageBox.Show("全参加者にルールを適用しました。", "ルールを適用しました", MessageBoxButton.OK);
        }
    }
}
