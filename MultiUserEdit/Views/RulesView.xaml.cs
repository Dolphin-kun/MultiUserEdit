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
            GlobalAddItemsCheck.Value = perm.CanAddItems;
            GlobalMoveItemsCheck.Value = perm.CanMoveItems;
            GlobalDeleteItemsCheck.Value = perm.CanDeleteItems;
            GlobalDeleteOthersCheck.Value = perm.CanDeleteOthersItems;
            GlobalEditPropsCheck.Value = perm.CanEditProperties;
            GlobalAddScenesCheck.Value = perm.CanAddScenes;
            GlobalDeleteScenesCheck.Value = perm.CanDeleteScenes;
            GlobalShareFilesCheck.Value = perm.CanShareFiles;
            GlobalShareVideosCheck.Value = perm.CanShareVideos;
            GlobalShareImagesCheck.Value = perm.CanShareImages;
            GlobalShareAudioCheck.Value = perm.CanShareAudio;
            GlobalSyncSeekCheck.Value = perm.CanSyncSeekPosition;
        }

        private static string StateText(bool allowed) => allowed ? Texts.Allowed : Texts.Restricted;

        private void LoadGuestPermissionStatus(UserPermission perm)
        {
            GuestLevelText.Text = perm.Level switch
            {
                PermissionLevel.Full => Texts.FullEdit,
                PermissionLevel.Standard => Texts.StandardEdit,
                PermissionLevel.ReadOnly => Texts.ReadOnly,
                PermissionLevel.LiveMirror => Texts.LiveMirror,
                _ => Texts.Unknown
            };

            GuestAddText.Text = StateText(perm.CanAddItems);
            GuestMoveText.Text = StateText(perm.CanMoveItems);
            GuestDeleteText.Text = StateText(perm.CanDeleteItems);
            GuestDeleteOthersText.Text = StateText(perm.CanDeleteItems && perm.CanDeleteOthersItems);
            GuestPropsText.Text = StateText(perm.CanEditProperties);
            GuestAddSceneText.Text = StateText(perm.CanAddScenes);
            GuestDeleteSceneText.Text = StateText(perm.CanDeleteScenes);
            GuestShareFilesText.Text = StateText(perm.CanShareFiles);
            GuestShareVideosText.Text = StateText(perm.CanShareFiles && perm.CanShareVideos);
            GuestShareImagesText.Text = StateText(perm.CanShareFiles && perm.CanShareImages);
            GuestShareAudioText.Text = StateText(perm.CanShareFiles && perm.CanShareAudio);
            GuestSyncSeekText.Text = perm.CanSyncSeekPosition ? Texts.On : Texts.Off;
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
                CanAddItems = GlobalAddItemsCheck.Value,
                CanMoveItems = GlobalMoveItemsCheck.Value,
                CanDeleteItems = GlobalDeleteItemsCheck.Value,
                CanDeleteOthersItems = GlobalDeleteOthersCheck.Value,
                CanEditProperties = GlobalEditPropsCheck.Value,
                CanAddScenes = GlobalAddScenesCheck.Value,
                CanDeleteScenes = GlobalDeleteScenesCheck.Value,
                CanShareFiles = GlobalShareFilesCheck.Value,
                CanShareVideos = GlobalShareVideosCheck.Value,
                CanShareImages = GlobalShareImagesCheck.Value,
                CanShareAudio = GlobalShareAudioCheck.Value,
                CanSyncSeekPosition = GlobalSyncSeekCheck.Value
            };

            viewModel.UpdateGlobalPermission(newPerm);
            MessageBox.Show(Texts.RulesAppliedMessage, Texts.RulesAppliedTitle, MessageBoxButton.OK);
        }
    }
}
