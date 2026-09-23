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

namespace MultiUserEdit.ViewModels
{
    public class MultiUserEditViewModel : Bindable, ITimelineToolViewModel, IToolViewModel, IDisposable
    {
        private bool disposed;
        private ToolState toolState = new();

        public string Title => string.Empty;

        public bool CanSuspend => true;

#pragma warning disable CS0067
        public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested;
#pragma warning restore CS0067

        public ToolState SaveState()
        {
            var state = CurrentSession?.CaptureParticipationState();
            toolState = toolState.WithSavedState(state);
            return toolState;
        }

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

        private readonly System.Threading.Lock fileTransferCompletedLock = new();
        private Action<string, string>? fileTransferCompletedHandlers;
        public event Action<string, string>? FileTransferCompleted
        {
            add { lock (fileTransferCompletedLock) fileTransferCompletedHandlers += value; }
            remove { lock (fileTransferCompletedLock) fileTransferCompletedHandlers -= value; }
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
        public ObservableCollection<TransferItemInfo> ActiveTransfers => CurrentSession?.ActiveTransfers ?? [];

        public ICommand? CreateRoomCommand => CurrentSession?.CreateRoomCommand;
        public ICommand? JoinRoomCommand => CurrentSession?.JoinRoomCommand;
        public ICommand? CopyRoomIdCommand => CurrentSession?.CopyRoomIdCommand;
        public ICommand? CopyInviteLinkCommand => CurrentSession?.CopyInviteLinkCommand;
        public ICommand? JoinFromClipboardCommand => CurrentSession?.JoinFromClipboardCommand;
        public ICommand? RegisterProtocolCommand => CurrentSession?.RegisterProtocolCommand;
        public ICommand? UnregisterProtocolCommand => CurrentSession?.UnregisterProtocolCommand;
        public ICommand? DisconnectCommand => CurrentSession?.DisconnectCommand;
        public ICommand? SyncNowCommand => CurrentSession?.SyncNowCommand;
        public ICommand? KickUserCommand => CurrentSession?.KickUserCommand;

        public bool IsProtocolRegistered => CurrentSession?.IsProtocolRegistered ?? ProtocolRegister.IsRegistered();

        public Guid LocalUserId => CurrentSession?.LocalUserId ?? Guid.Empty;

        public string RoomId
        {
            get => CurrentSession?.RoomId ?? string.Empty;
            set => CurrentSession?.RoomId = value;
        }

        public string InputRoomId
        {
            get => CurrentSession?.InputRoomId ?? string.Empty;
            set => CurrentSession?.InputRoomId = value;
        }

        public string UserName
        {
            get => CurrentSession?.UserName ?? Settings.MultiUserEditSettings.Default.UserName;
            set
            {
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

        public double? GetTotalParticipationSeconds(Participant participant) =>
            CurrentSession?.GetTotalParticipationSeconds(participant);

        public bool IsHost => CurrentSession?.IsHost ?? false;

        public bool IsTransferring
        {
            get => CurrentSession?.IsTransferring ?? false;
            set => CurrentSession?.IsTransferring = value;
        }

        public string TransferStatusText
        {
            get => CurrentSession?.TransferStatusText ?? string.Empty;
            set => CurrentSession?.TransferStatusText = value;
        }

        public double TransferProgress
        {
            get => CurrentSession?.TransferProgress ?? 0;
            set => CurrentSession?.TransferProgress = value;
        }

        public UserPermission GlobalDefaultPermission => CurrentSession?.GlobalDefaultPermission ?? new();

        public UserPermission CurrentUserPermission
        {
            get => CurrentSession?.CurrentUserPermission ?? new();
            set => CurrentSession?.CurrentUserPermission = value;
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
                    CurrentSession.SentFilesChanged -= OnSessionSentFilesChanged;
                }

                CurrentSession = SessionManager.GetOrCreate(info.Scenes);
                CurrentSession.PropertyChanged += OnSessionPropertyChanged;
                CurrentSession.FileTransferCompleted += OnSessionFileTransferCompleted;
                CurrentSession.SentFilesChanged += OnSessionSentFilesChanged;

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

        public event Action? SentFilesChanged;

        internal IReadOnlyList<SentFileRecord> GetSentFiles() => CurrentSession?.GetSentFiles() ?? [];

        private void OnSessionSentFilesChanged() => SentFilesChanged?.Invoke();

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

        public void ApplySyncScenes(OnlineScenes onlineScenes, Guid ownerId, bool isManual)
        {
            CurrentSession?.ApplySyncScenes(onlineScenes, ownerId, isManual);
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

        internal void HandleCharacterRequest(CharacterRequestEvent evt)
        {
            CurrentSession?.HandleCharacterRequest(evt);
        }

        internal void HandleCharacterShared(CharacterSharedEvent evt)
        {
            CurrentSession?.HandleCharacterShared(evt);
        }

        internal void HandleItemStateRequest(Commons.Events.ItemStateRequestEvent evt)
        {
            CurrentSession?.HandleItemStateRequest(evt);
        }

        internal void HandleStateDigestRequest(Commons.Events.StateDigestRequestEvent evt)
        {
            CurrentSession?.HandleStateDigestRequest(evt);
        }

        internal void HandleStateDigest(Commons.Events.StateDigestEvent evt)
        {
            CurrentSession?.HandleStateDigest(evt);
        }

        internal void HandleMissingResource(Commons.Events.MissingResourceEvent evt)
        {
            CurrentSession?.HandleMissingResource(evt);
        }

        internal void SyncFrom(Guid sourceUserId)
        {
            CurrentSession?.SyncFrom(sourceUserId);
        }

        internal bool IsReceivingFiles() => CurrentSession?.IsReceivingFiles() ?? false;

        internal string GetUserName(Guid userId) =>
            Participants.FirstOrDefault(p => p.UserId == userId)?.UserName ?? string.Empty;

        internal bool IsCharacterDecided(string characterName) =>
            CurrentSession == null || CollaborationSession.IsCharacterDecided(characterName);

        internal void RequestCharacter(string characterName, Guid ownerId, Action resume)
        {
            CurrentSession?.RequestCharacter(characterName, ownerId, resume);
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

        internal void DetachFromSession()
        {
            if (CurrentSession == null) return;

            CurrentSession.PropertyChanged -= OnSessionPropertyChanged;
            CurrentSession.FileTransferCompleted -= OnSessionFileTransferCompleted;
            CurrentSession.SentFilesChanged -= OnSessionSentFilesChanged;
            CurrentSession = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            GC.SuppressFinalize(this);
        }
    }
}