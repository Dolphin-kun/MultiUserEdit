using MultiUserEdit.Commons.Models;
using System.Windows;

namespace MultiUserEdit.Views
{
    public partial class UserPermissionDialog : Window
    {
        public UserPermission ResultPermission { get; private set; }

        public UserPermissionDialog(string targetUserName, UserPermission currentPermission)
        {
            InitializeComponent();

            TargetUserText.Text = $"{targetUserName} の操作権限設定";
            ResultPermission = new UserPermission
            {
                Level = currentPermission.Level,
                CanAddItems = currentPermission.CanAddItems,
                CanMoveItems = currentPermission.CanMoveItems,
                CanDeleteItems = currentPermission.CanDeleteItems,
                CanEditProperties = currentPermission.CanEditProperties,
                CanManageScenes = currentPermission.CanManageScenes,
                CanSyncSeekPosition = currentPermission.CanSyncSeekPosition
            };

            LoadPermissionToUi(ResultPermission);
        }

        private void LoadPermissionToUi(UserPermission perm)
        {
            FullRadio.IsChecked = perm.Level == PermissionLevel.Full;
            StandardRadio.IsChecked = perm.Level == PermissionLevel.Standard;
            ReadOnlyRadio.IsChecked = perm.Level == PermissionLevel.ReadOnly;
            LiveMirrorRadio.IsChecked = perm.Level == PermissionLevel.LiveMirror;

            AddItemsCheck.IsChecked = perm.CanAddItems;
            MoveItemsCheck.IsChecked = perm.CanMoveItems;
            DeleteItemsCheck.IsChecked = perm.CanDeleteItems;
            EditPropsCheck.IsChecked = perm.CanEditProperties;
            ManageScenesCheck.IsChecked = perm.CanManageScenes;
            SyncSeekCheck.IsChecked = perm.CanSyncSeekPosition;
        }

        private void OnPresetChecked(object sender, RoutedEventArgs e)
        {
            PermissionLevel level;
            if (FullRadio.IsChecked == true) level = PermissionLevel.Full;
            else if (StandardRadio.IsChecked == true) level = PermissionLevel.Standard;
            else if (ReadOnlyRadio.IsChecked == true) level = PermissionLevel.ReadOnly;
            else level = PermissionLevel.LiveMirror;

            var preset = UserPermission.CreateFromLevel(level);
            AddItemsCheck.IsChecked = preset.CanAddItems;
            MoveItemsCheck.IsChecked = preset.CanMoveItems;
            DeleteItemsCheck.IsChecked = preset.CanDeleteItems;
            EditPropsCheck.IsChecked = preset.CanEditProperties;
            ManageScenesCheck.IsChecked = preset.CanManageScenes;
            SyncSeekCheck.IsChecked = preset.CanSyncSeekPosition;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            PermissionLevel level;
            if (FullRadio.IsChecked == true) level = PermissionLevel.Full;
            else if (StandardRadio.IsChecked == true) level = PermissionLevel.Standard;
            else if (ReadOnlyRadio.IsChecked == true) level = PermissionLevel.ReadOnly;
            else level = PermissionLevel.LiveMirror;

            ResultPermission.Level = level;
            ResultPermission.CanAddItems = AddItemsCheck.IsChecked == true;
            ResultPermission.CanMoveItems = MoveItemsCheck.IsChecked == true;
            ResultPermission.CanDeleteItems = DeleteItemsCheck.IsChecked == true;
            ResultPermission.CanEditProperties = EditPropsCheck.IsChecked == true;
            ResultPermission.CanManageScenes = ManageScenesCheck.IsChecked == true;
            ResultPermission.CanSyncSeekPosition = SyncSeekCheck.IsChecked == true;

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
