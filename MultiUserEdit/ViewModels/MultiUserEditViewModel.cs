using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.ViewModels
{
    public class MultiUserEditViewModel : Bindable, ITimelineToolViewModel, IToolViewModel, IDisposable
    {
        private bool disposed;
        private ToolState toolState = new();

        // ツールのタイトルはプラグイン既定のものを使うため空を返す
        public string Title => string.Empty;

        // パネルを非表示にしてもViewModelを破棄させない（共同編集のセッションを維持するため）
        public bool CanSuspend => false;

        // IToolViewModelの実装に必要だが、このプラグインはビューを複数作らないため発火しない
#pragma warning disable CS0067
        public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested;
#pragma warning restore CS0067

        // プロジェクト保存時にYMM4から呼ばれる。合計参加時間をプロジェクトファイルへ書き出す。
        public ToolState SaveState()
        {
            var state = CurrentSession?.CaptureParticipationState();
            toolState = toolState.WithSavedState(state);
            return toolState;
        }

        // プロジェクト読み込み時にYMM4から呼ばれる。
        // セッション生成前に呼ばれることもあるため、保持しておいて生成時に反映する。
        public void LoadState(ToolState stateData)
        {
            toolState = stateData;
            pendingParticipationState = stateData.GetSavedState<ProjectParticipationState>();
            CurrentSession?.RestoreParticipationState(pendingParticipationState);
        }

        private ProjectParticipationState? pendingParticipationState;

        public CollaborationSession? CurrentSession { get; private set; }

        internal SessionClient? SessionClient => CurrentSession?.SessionClient;
        public AdornerManager? AdornerManager => CurrentSession?.AdornerManager;
        internal EditEventSender? EventSender => CurrentSession?.EventSender;
        internal TimelineSyncManager? TimelineSyncManager => CurrentSession?.TimelineSyncManager;

        public Scenes? Scenes => CurrentSession?.Scenes;
        internal Timeline? FirstOrDefaultTimeline => CurrentSession?.FirstOrDefaultTimeline;

        public Dictionary<Guid, Guid> LockedItems => CurrentSession?.LockedItems ?? [];

        private Action<string, string>? fileTransferCompletedHandlers;
        public event Action<string, string>? FileTransferCompleted
        {
            add { fileTransferCompletedHandlers += value; }
            remove { fileTransferCompletedHandlers -= value; }
        }

        private Func<int, bool, Task>? pendingPreviewSeekAction;
        private Func<bool>? pendingIsPlayingFunc;

        public void SetPreviewSeekAction(Func<int, bool, Task> action, Func<bool>? isPlayingFunc = null)
        {
            pendingPreviewSeekAction = action;
            pendingIsPlayingFunc = isPlayingFunc;
            CurrentSession?.SetPreviewSeekAction(action, isPlayingFunc);
        }

        public ObservableCollection<string> ReceivedMessages => CurrentSession?.ReceivedMessages ?? [];
        public ObservableCollection<Participant> Participants => CurrentSession?.Participants ?? [];

        public ICommand? CreateRoomCommand => CurrentSession?.CreateRoomCommand;
        public ICommand? JoinRoomCommand => CurrentSession?.JoinRoomCommand;
        public ICommand? CopyRoomIdCommand => CurrentSession?.CopyRoomIdCommand;
        public ICommand? CopyInviteLinkCommand => CurrentSession?.CopyInviteLinkCommand;
        public ICommand? JoinFromClipboardCommand => CurrentSession?.JoinFromClipboardCommand;
        public ICommand? RegisterProtocolCommand => CurrentSession?.RegisterProtocolCommand;
        public ICommand? UnregisterProtocolCommand => CurrentSession?.UnregisterProtocolCommand;
        public ICommand? DisconnectCommand => CurrentSession?.DisconnectCommand;
        public ICommand? KickUserCommand => CurrentSession?.KickUserCommand;

        public bool IsProtocolRegistered => CurrentSession?.IsProtocolRegistered ?? ProtocolRegister.IsRegistered();

        public Guid LocalUserId => CurrentSession?.LocalUserId ?? Guid.Empty;

        public string RoomId
        {
            get => CurrentSession?.RoomId ?? string.Empty;
            set { if (CurrentSession != null) CurrentSession.RoomId = value; }
        }

        public string InputRoomId
        {
            get => CurrentSession?.InputRoomId ?? string.Empty;
            set { if (CurrentSession != null) CurrentSession.InputRoomId = value; }
        }

        public string UserName
        {
            get => CurrentSession?.UserName ?? Settings.MultiUserEditSettings.Default.UserName;
            set
            {
                // セッション生成前でも設定画面から編集できるようにフォールバックする
                if (CurrentSession != null) CurrentSession.UserName = value;
                else Settings.MultiUserEditSettings.Default.UserName = value;
            }
        }

        public string UserDescription
        {
            get => CurrentSession?.UserDescription ?? Settings.MultiUserEditSettings.Default.UserDescription;
            set
            {
                if (CurrentSession != null) CurrentSession.UserDescription = value;
                else Settings.MultiUserEditSettings.Default.UserDescription = ProfileText.NormalizeDescription(value);
            }
        }

        // 参加者の合計参加時間（秒）。プロジェクトに記録が無い場合はnull。
        public double? GetTotalParticipationSeconds(Participant participant) =>
            CurrentSession?.GetTotalParticipationSeconds(participant);

        public bool IsHost => CurrentSession?.IsHost ?? false;

        public bool IsTransferring
        {
            get => CurrentSession?.IsTransferring ?? false;
            set { if (CurrentSession != null) CurrentSession.IsTransferring = value; }
        }

        public string TransferStatusText
        {
            get => CurrentSession?.TransferStatusText ?? string.Empty;
            set { if (CurrentSession != null) CurrentSession.TransferStatusText = value; }
        }

        public double TransferProgress
        {
            get => CurrentSession?.TransferProgress ?? 0;
            set { if (CurrentSession != null) CurrentSession.TransferProgress = value; }
        }

        public UserPermission GlobalDefaultPermission => CurrentSession?.GlobalDefaultPermission ?? new();

        public UserPermission CurrentUserPermission
        {
            get => CurrentSession?.CurrentUserPermission ?? new();
            set { if (CurrentSession != null) CurrentSession.CurrentUserPermission = value; }
        }

        public bool IsConnected => CurrentSession?.IsConnected ?? false;

        public MultiUserEditViewModel()
        {
        }

        public void SetTimelineViewReference(YukkuriMovieMaker.Views.TimelineView timelineView)
        {
            CurrentSession?.SetTimelineViewReference(timelineView);
        }

        public void SetTimelineToolInfo(TimelineToolInfo info)
        {
            if (info.Scenes != null)
            {
                if (CurrentSession != null)
                {
                    CurrentSession.PropertyChanged -= OnSessionPropertyChanged;
                    CurrentSession.FileTransferCompleted -= OnSessionFileTransferCompleted;
                }

                CurrentSession = SessionManager.GetOrCreate(info.Scenes);
                CurrentSession.PropertyChanged += OnSessionPropertyChanged;
                CurrentSession.FileTransferCompleted += OnSessionFileTransferCompleted;

                if (pendingPreviewSeekAction != null)
                    CurrentSession.SetPreviewSeekAction(pendingPreviewSeekAction, pendingIsPlayingFunc);

                if (pendingParticipationState != null)
                    CurrentSession.RestoreParticipationState(pendingParticipationState);

                CurrentSession.SetTimelineToolInfo(info, this);
            }

            OnPropertyChanged(string.Empty);
        }

        private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e.PropertyName ?? string.Empty);
        }

        private void OnSessionFileTransferCompleted(string transferId, string path)
        {
            fileTransferCompletedHandlers?.Invoke(transferId, path);
        }

        public void ExecuteRemoteAction(Action action)
        {
            CurrentSession?.ExecuteRemoteAction(action);
        }

        public void UpdateGlobalPermission(UserPermission newPerm)
        {
            CurrentSession?.UpdateGlobalPermission(newPerm);
        }

        public void UpdateUserPermission(Guid targetUserId, UserPermission newPerm)
        {
            CurrentSession?.UpdateUserPermission(targetUserId, newPerm);
        }

        internal void HandlePermissionUpdated(PermissionUpdatedEvent evt)
        {
            CurrentSession?.HandlePermissionUpdated(evt);
        }

        internal void HandleSceneAdded(SceneAddedEvent evt)
        {
            CurrentSession?.HandleSceneAdded(evt);
        }

        internal void HandleSceneRemoved(SceneRemovedEvent evt)
        {
            CurrentSession?.HandleSceneRemoved(evt);
        }

        public static string ExtractRoomId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            input = input.Trim();

            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var id = query["roomId"] ?? query["id"];
                if (!string.IsNullOrEmpty(id)) return id;
            }

            if (input.Contains("roomId="))
            {
                var parts = input.Split("roomId=");
                if (parts.Length > 1)
                {
                    var id = parts[1].Split('&')[0];
                    return id;
                }
            }

            return input;
        }

        public void ApplySyncScenes(OnlineScenes onlineScenes)
        {
            CurrentSession?.ApplySyncScenes(onlineScenes);
        }

        internal void HandlePresenceEvent(PresenceEvent evt)
        {
            CurrentSession?.HandlePresenceEvent(evt);
        }

        internal void HandleSyncRequestEvent(SyncRequestEvent evt)
        {
            CurrentSession?.HandleSyncRequestEvent(evt);
        }

        internal void HandleUserLeftEvent(UserLeftEvent evt)
        {
            CurrentSession?.HandleUserLeftEvent(evt);
        }

        internal void HandleUserKickedEvent(UserKickedEvent evt)
        {
            CurrentSession?.HandleUserKickedEvent(evt);
        }

        internal void HandleItemLockedEvent(ItemLockedEvent evt)
        {
            CurrentSession?.HandleItemLockedEvent(evt);
        }

        internal void HandleItemUnlockedEvent(ItemUnlockedEvent evt)
        {
            CurrentSession?.HandleItemUnlockedEvent(evt);
        }

        internal void HandleCursorMovedEvent(CursorMovedEvent evt)
        {
            CurrentSession?.HandleCursorMovedEvent(evt);
        }

        internal void HandleFileAvailable(FileAvailableEvent evt)
        {
            CurrentSession?.HandleFileAvailable(evt);
        }

        internal void HandleFileRequest(FileRequestEvent evt)
        {
            CurrentSession?.HandleFileRequest(evt);
        }

        internal void HandleFileTransferStart(FileTransferStartEvent evt)
        {
            CurrentSession?.HandleFileTransferStart(evt);
        }

        internal void HandleFileChunk(FileChunkEvent evt)
        {
            CurrentSession?.HandleFileChunk(evt);
        }

        public Task SendFileAsync(string filePath) =>
            CurrentSession?.SendFileAsync(filePath) ?? Task.CompletedTask;

        public void LockItemLocally(Guid itemId)
        {
            CurrentSession?.LockItemLocally(itemId);
        }

        public void UnlockItemLocally(Guid itemId)
        {
            CurrentSession?.UnlockItemLocally(itemId);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (CurrentSession != null)
            {
                CurrentSession.PropertyChanged -= OnSessionPropertyChanged;
                CurrentSession.FileTransferCompleted -= OnSessionFileTransferCompleted;
                CurrentSession = null;
            }

            GC.SuppressFinalize(this);
        }
    }
}