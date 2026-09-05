using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Models;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MultiUserEdit.Views
{
    public partial class UserProfileDialog : Window
    {
        private readonly Participant participant;
        private readonly bool isSelf;
        private readonly bool canManage;
        private readonly Func<double?> getTotalSeconds;
        private readonly DispatcherTimer durationTimer;

        public UserPermission ResultPermission { get; }
        public string ResultUserName { get; private set; }
        public string ResultDescription { get; private set; }
        public bool KickRequested { get; private set; }

        /// <param name="isSelf">対象が自分自身か（プロフィールを編集できる）</param>
        /// <param name="canManage">自分がホストで対象がゲストか（権限の変更とキックができる）</param>
        public UserProfileDialog(Participant participant, bool isSelf, bool canManage, Func<double?> getTotalSeconds)
        {
            InitializeComponent();

            this.participant = participant;
            this.isSelf = isSelf;
            this.canManage = canManage;
            this.getTotalSeconds = getTotalSeconds;

            ResultUserName = participant.UserName;
            ResultDescription = participant.Description;
            ResultPermission = new UserPermission
            {
                Level = participant.Permission.Level,
                CanAddItems = participant.Permission.CanAddItems,
                CanMoveItems = participant.Permission.CanMoveItems,
                CanDeleteItems = participant.Permission.CanDeleteItems,
                CanEditProperties = participant.Permission.CanEditProperties,
                CanManageScenes = participant.Permission.CanManageScenes,
                CanSyncSeekPosition = participant.Permission.CanSyncSeekPosition
            };

            InitializeHeader();
            InitializeProfileTab();
            InitializePermissionTab();
            UpdateDurations();
            UpdateStatus();

            // 表示中は在席状態・編集時間・合計参加時間を毎秒更新する
            durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            durationTimer.Tick += (s, e) => { UpdateDurations(); UpdateStatus(); };
            durationTimer.Start();
            Closed += (s, e) => durationTimer.Stop();
        }

        private void InitializeHeader()
        {
            Title = $"{participant.UserName} の情報";
            UserNameText.Text = participant.UserName;
            ColorSwatch.Fill = new SolidColorBrush(participant.ThemeColor);
            RoleText.Text = participant.Role == UserRole.Host ? "ホスト" : "ゲスト";

            DescriptionText.Text = ProfileText.ToSingleLine(participant.Description);
            DescriptionText.Visibility = string.IsNullOrWhiteSpace(participant.Description)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void InitializeProfileTab()
        {
            UserNameBox.Text = participant.UserName;
            DescriptionBox.Text = participant.Description;
            DescriptionBox.MaxLength = ProfileText.MaxDescriptionLength;
            DescriptionHint.Text = ProfileText.DescriptionHint;

            UserNameBox.IsEnabled = isSelf;
            DescriptionBox.IsEnabled = isSelf;
            DescriptionHint.Visibility = isSelf ? Visibility.Visible : Visibility.Collapsed;
            ReadOnlyNote.Visibility = isSelf ? Visibility.Collapsed : Visibility.Visible;
        }

        private void InitializePermissionTab()
        {
            // 権限タブとキックボタンは、自分がホストで相手がゲストのときだけ操作できる
            PermissionTab.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;
            KickButton.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;

            if (!canManage) return;

            LoadPermissionToUi(ResultPermission);
        }

        // オンライン／離席中／オフラインを参加者一覧と同じ色・文言で表示する
        private void UpdateStatus()
        {
            StatusDot.Fill = participant.StatusBrush;
            StatusLabel.Text = participant.StatusText;
        }

        private void UpdateDurations()
        {
            EditDurationText.Text = DurationFormatter.Format(DateTime.Now - participant.JoinedAt);

            var total = getTotalSeconds();
            if (total is null)
            {
                TotalDurationPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TotalDurationPanel.Visibility = Visibility.Visible;
                TotalDurationText.Text = DurationFormatter.FormatSeconds(total.Value);
            }
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

        private PermissionLevel GetSelectedLevel()
        {
            if (FullRadio.IsChecked == true) return PermissionLevel.Full;
            if (StandardRadio.IsChecked == true) return PermissionLevel.Standard;
            if (ReadOnlyRadio.IsChecked == true) return PermissionLevel.ReadOnly;
            return PermissionLevel.LiveMirror;
        }

        private void OnPresetChecked(object sender, RoutedEventArgs e)
        {
            var preset = UserPermission.CreateFromLevel(GetSelectedLevel());
            AddItemsCheck.IsChecked = preset.CanAddItems;
            MoveItemsCheck.IsChecked = preset.CanMoveItems;
            DeleteItemsCheck.IsChecked = preset.CanDeleteItems;
            EditPropsCheck.IsChecked = preset.CanEditProperties;
            ManageScenesCheck.IsChecked = preset.CanManageScenes;
            SyncSeekCheck.IsChecked = preset.CanSyncSeekPosition;
        }

        private void OnKickClick(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"「{participant.UserName}」をルームから退出させますか？",
                "ユーザーのキック",
                MessageBoxButton.OKCancel);

            if (result != MessageBoxResult.OK) return;

            KickRequested = true;
            DialogResult = true;
            Close();
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (isSelf)
            {
                var name = UserNameBox.Text.Trim();
                ResultUserName = string.IsNullOrEmpty(name) ? participant.UserName : name;
                ResultDescription = ProfileText.NormalizeDescription(DescriptionBox.Text);
            }

            if (canManage)
            {
                ResultPermission.Level = GetSelectedLevel();
                ResultPermission.CanAddItems = AddItemsCheck.IsChecked == true;
                ResultPermission.CanMoveItems = MoveItemsCheck.IsChecked == true;
                ResultPermission.CanDeleteItems = DeleteItemsCheck.IsChecked == true;
                ResultPermission.CanEditProperties = EditPropsCheck.IsChecked == true;
                ResultPermission.CanManageScenes = ManageScenesCheck.IsChecked == true;
                ResultPermission.CanSyncSeekPosition = SyncSeekCheck.IsChecked == true;
            }

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
