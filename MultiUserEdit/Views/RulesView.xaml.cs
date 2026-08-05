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
            if (viewModel != null)
            {
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

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

            GlobalAddItemsCheck.IsChecked = perm.CanAddItems;
            GlobalMoveItemsCheck.IsChecked = perm.CanMoveItems;
            GlobalDeleteItemsCheck.IsChecked = perm.CanDeleteItems;
            GlobalEditPropsCheck.IsChecked = perm.CanEditProperties;
            GlobalManageScenesCheck.IsChecked = perm.CanManageScenes;
            GlobalSyncSeekCheck.IsChecked = perm.CanSyncSeekPosition;
        }

        private void LoadGuestPermissionStatus(UserPermission perm)
        {
            GuestLevelText.Text = perm.Level switch
            {
                PermissionLevel.Full => "フル編集 (全操作許可)",
                PermissionLevel.Standard => "標準編集 (一部操作制限)",
                PermissionLevel.ReadOnly => "閲覧専用 (編集不可)",
                PermissionLevel.LiveMirror => "ライブミラーリング (完全追従)",
                _ => "閲覧専用"
            };

            GuestAddText.Text = perm.CanAddItems ? "許可" : "制限中 (追加不可)";
            GuestMoveText.Text = perm.CanMoveItems ? "許可" : "制限中 (移動不可)";
            GuestDeleteText.Text = perm.CanDeleteItems ? "許可" : "制限中 (削除不可)";
            GuestPropsText.Text = perm.CanEditProperties ? "許可" : "制限中 (編集不可)";
            GuestManageScenesText.Text = perm.CanManageScenes ? "許可" : "制限中 (操作不可)";
            GuestSyncSeekText.Text = perm.CanSyncSeekPosition ? "有効 (ホストに自動追従中)" : "オフ";
        }

        private void OnGlobalPresetChecked(object sender, RoutedEventArgs e)
        {
            PermissionLevel level;
            if (GlobalFullRadio.IsChecked == true) level = PermissionLevel.Full;
            else if (GlobalStandardRadio.IsChecked == true) level = PermissionLevel.Standard;
            else if (GlobalReadOnlyRadio.IsChecked == true) level = PermissionLevel.ReadOnly;
            else level = PermissionLevel.LiveMirror;

            var preset = UserPermission.CreateFromLevel(level);
            GlobalAddItemsCheck.IsChecked = preset.CanAddItems;
            GlobalMoveItemsCheck.IsChecked = preset.CanMoveItems;
            GlobalDeleteItemsCheck.IsChecked = preset.CanDeleteItems;
            GlobalEditPropsCheck.IsChecked = preset.CanEditProperties;
            GlobalManageScenesCheck.IsChecked = preset.CanManageScenes;
            GlobalSyncSeekCheck.IsChecked = preset.CanSyncSeekPosition;
        }

        private void OnApplyGlobalRulesClick(object sender, RoutedEventArgs e)
        {
            if (viewModel == null) return;

            PermissionLevel level;
            if (GlobalFullRadio.IsChecked == true) level = PermissionLevel.Full;
            else if (GlobalStandardRadio.IsChecked == true) level = PermissionLevel.Standard;
            else if (GlobalReadOnlyRadio.IsChecked == true) level = PermissionLevel.ReadOnly;
            else level = PermissionLevel.LiveMirror;

            var newPerm = new UserPermission
            {
                Level = level,
                CanAddItems = GlobalAddItemsCheck.IsChecked == true,
                CanMoveItems = GlobalMoveItemsCheck.IsChecked == true,
                CanDeleteItems = GlobalDeleteItemsCheck.IsChecked == true,
                CanEditProperties = GlobalEditPropsCheck.IsChecked == true,
                CanManageScenes = GlobalManageScenesCheck.IsChecked == true,
                CanSyncSeekPosition = GlobalSyncSeekCheck.IsChecked == true
            };

            viewModel.UpdateGlobalPermission(newPerm);
            MessageBox.Show("全体ルールを全参加者に適用しました。", "ルール適用完了");
        }
    }
}
