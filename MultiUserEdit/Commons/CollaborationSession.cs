using MultiUserEdit.Commons.EventHandlers;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using MultiUserEdit.ViewModels;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.UndoRedo;

namespace MultiUserEdit.Commons
{
    public class CollaborationSession : Bindable, IDisposable
    {
        private readonly SessionClient sessionClient;
        internal SessionClient SessionClient => sessionClient;

        private readonly ClientEventDispatcher eventDispatcher;
        internal ClientEventDispatcher EventDispatcher => eventDispatcher;

        private readonly AdornerManager adornerManager;
        public AdornerManager AdornerManager => adornerManager;

        private readonly FileTransferManager fileTransferManager;
        internal FileTransferManager FileTransferManager => fileTransferManager;

        private readonly EditEventSender eventSender;
        internal EditEventSender EventSender => eventSender;

        private readonly CharacterShareManager characterShareManager;

        private readonly TimelineSyncManager timelineSyncManager;
        internal TimelineSyncManager TimelineSyncManager => timelineSyncManager;

        private readonly System.Windows.Threading.DispatcherTimer presenceTimer;

        private bool isConnected;
        private bool disposed;
        private bool isApplyingRemoteEvent;

        private UndoRedoManager? undoRedoManager;
        public Scenes Scenes { get; private set; }

        private Timeline? firstOrDefaultTimeline;
        internal Timeline? FirstOrDefaultTimeline
        {
            get => firstOrDefaultTimeline;
            private set
            {
                firstOrDefaultTimeline?.PropertyChanged -= FirstOrDefaultTimeline_PropertyChanged;
                firstOrDefaultTimeline = value;
                firstOrDefaultTimeline?.PropertyChanged += FirstOrDefaultTimeline_PropertyChanged;
            }
        }

        private List<Timeline> scenesSnapshot = [];

        public Dictionary<Guid, Guid> LockedItems { get; } = [];

        public Dictionary<Guid, OperatingItem> OperatingItems { get; } = [];
        public event Action? OperatingItemsChanged;

        private void NoteRemoteOperation(EditEvent editEvent)
        {
            switch (editEvent)
            {
                case ItemMovedEvent moved:
                    OperatingItems[moved.ItemId] = new OperatingItem(editEvent.ExecutorId, moved.TimelineIndex, DateTime.UtcNow);
                    break;
                case ItemUpdatedEvent updated:
                    OperatingItems[updated.ItemId] = new OperatingItem(editEvent.ExecutorId, updated.TimelineIndex, DateTime.UtcNow);
                    break;
                case ItemRemovedEvent removed:
                    if (!OperatingItems.Remove(removed.ItemId)) return;
                    break;
                default:
                    return;
            }

            OperatingItemsChanged?.Invoke();
        }
        private readonly Dictionary<Guid, long> locallyLockedItems = [];
        private readonly Dictionary<Guid, DateTime> lockedItemsReceivedAt = [];

        public event Action<string, string>? FileTransferCompleted;
        public event Action? SentFilesChanged;

        internal IReadOnlyList<SentFileRecord> GetSentFiles() => fileTransferManager.GetSentFiles();

        private Func<int, bool, Task>? previewSeekAction;
        private Func<bool>? getIsPlayingFunc;
        public void SetPreviewSeekAction(Func<int, bool, Task> action, Func<bool>? isPlayingFunc = null)
        {
            previewSeekAction = action;
            getIsPlayingFunc = isPlayingFunc;
        }

        public ObservableCollection<string> ReceivedMessages { get; } = [];
        public ObservableCollection<Participant> Participants { get; } = [];

        private ProjectParticipationState participationState = new();

        private void AddParticipantSorted(Participant participant)
        {
            var index = participant.Role == UserRole.Host ? 0 : Participants.Count;
            Participants.Insert(index, participant);

            fileTransferManager.ForgetAnnouncedFiles();
        }

        private void MoveHostToTopIfNeeded(Participant participant)
        {
            if (participant.Role != UserRole.Host) return;

            var index = Participants.IndexOf(participant);
            if (index > 0) Participants.Move(index, 0);
        }

        private void CommitParticipationTime(Participant participant)
        {
            if (participant.ProfileId == Guid.Empty) return;

            var elapsed = (DateTime.Now - participant.JoinedAt).TotalSeconds;
            if (elapsed <= 0) return;

            var key = participant.ProfileId.ToString();
            if (!participationState.Participants.TryGetValue(key, out var record))
            {
                record = new ParticipationRecord();
                participationState.Participants[key] = record;
            }

            record.UserName = participant.UserName;
            record.TotalSeconds += elapsed;
            participant.JoinedAt = DateTime.Now;
        }

        public double? GetTotalParticipationSeconds(Participant participant)
        {
            if (participant.ProfileId == Guid.Empty) return null;

            var stored = participationState.Participants.TryGetValue(participant.ProfileId.ToString(), out var record)
                ? record.TotalSeconds
                : 0;

            return stored + Math.Max(0, (DateTime.Now - participant.JoinedAt).TotalSeconds);
        }

        internal ProjectParticipationState CaptureParticipationState()
        {
            foreach (var participant in Participants)
            {
                CommitParticipationTime(participant);
            }
            return participationState;
        }

        internal void RestoreParticipationState(ProjectParticipationState? state)
        {
            participationState = state ?? new ProjectParticipationState();
        }

        public ICommand CreateRoomCommand { get; }
        public ICommand JoinRoomCommand { get; }
        public ICommand CopyRoomIdCommand { get; }
        public ICommand CopyInviteLinkCommand { get; }
        public ICommand JoinFromClipboardCommand { get; }
        public ICommand RegisterProtocolCommand { get; }
        public ICommand UnregisterProtocolCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand SyncNowCommand { get; }
        public ICommand KickUserCommand { get; }

        private bool awaitingInitialSync;
        private Guid? pendingManualSyncSource;

        internal void SyncFrom(Guid sourceUserId)
        {
            if (!IsConnected || sourceUserId == LocalUserId) return;

            HasDivergenceWarning = false;

            var sourceName = GetParticipantName(sourceUserId);
            var confirmed = MessageBox.Show(
                SyncFromConfirmMessage(sourceName),
                "データの同期",
                MessageBoxButton.OKCancel) == MessageBoxResult.OK;
            if (!confirmed) return;

            RequestSyncFrom(sourceUserId);
        }

        private void RequestSyncFrom(Guid sourceUserId)
        {
            pendingManualSyncSource = sourceUserId;

            var request = new SyncRequestEvent(sourceUserId, IsManual: true)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            _ = sessionClient.SendAsync(sourceUserId.ToString(), request);
        }

        private string GetParticipantName(Guid userId)
        {
            var name = Participants.FirstOrDefault(p => p.UserId == userId)?.UserName;
            return string.IsNullOrWhiteSpace(name) ? "共同編集相手" : name;
        }

        private void SyncNow()
        {
            if (!IsConnected) return;

            if (!IsHost)
            {
                var host = Participants.FirstOrDefault(p => p.Role == UserRole.Host && p.UserId != LocalUserId);
                if (host != null) SyncFrom(host.UserId);
                return;
            }

            {
                var confirmed = MessageBox.Show(
                    "参加者全員のタイムラインを、自分のタイムラインの状態で上書きします。\n実行してもよろしいですか？",
                    "データの同期",
                    MessageBoxButton.OKCancel) == MessageBoxResult.OK;

                if (!confirmed) return;

                SendSyncScenes(null, isManual: true);
            }
        }

        private bool isProtocolRegistered = ProtocolRegister.IsRegistered();
        public bool IsProtocolRegistered
        {
            get => isProtocolRegistered;
            private set => Set(ref isProtocolRegistered, value);
        }

        public Guid LocalUserId { get; }
        private UserRole localUserRole;

        private string roomId = string.Empty;
        public string RoomId { get => roomId; set => Set(ref roomId, value); }

        private string inputRoomId = string.Empty;
        public string InputRoomId
        {
            get => inputRoomId;
            set
            {
                var cleanId = MultiUserEditViewModel.ExtractRoomId(value);
                Set(ref inputRoomId, cleanId);
                (JoinRoomCommand as ActionCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string UserName
        {
            get => Settings.MultiUserEditSettings.Default.UserName;
            set
            {
                if (Settings.MultiUserEditSettings.Default.UserName != value)
                {
                    Settings.MultiUserEditSettings.Default.UserName = value;
                    OnPropertyChanged(nameof(UserName));

                    var localParticipant = Participants.FirstOrDefault(p => p.UserId == LocalUserId);
                    localParticipant?.UserName = value;

                    BroadcastLocalPresence();
                }
            }
        }

        public string UserDescription
        {
            get => Settings.MultiUserEditSettings.Default.UserDescription;
            set
            {
                var normalized = ProfileText.NormalizeDescription(value);
                if (Settings.MultiUserEditSettings.Default.UserDescription != normalized)
                {
                    Settings.MultiUserEditSettings.Default.UserDescription = normalized;
                    OnPropertyChanged(nameof(UserDescription));

                    var localParticipant = Participants.FirstOrDefault(p => p.UserId == LocalUserId);
                    localParticipant?.Description = normalized;

                    BroadcastLocalPresence();
                }
            }
        }

        public static Guid LocalProfileId =>
            Guid.TryParse(Settings.MultiUserEditSettings.Default.ProfileId, out var id) ? id : Guid.Empty;

        private void BroadcastLocalPresence()
        {
            if (!IsConnected) return;

            var presenceEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, true, LocalProfileId, UserDescription, UpdateChecker.Instance.CurrentVersion)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            _ = sessionClient.SendAsync(null, presenceEvt);
        }

        private bool isHost;
        public bool IsHost
        {
            get => isHost;
            private set => Set(ref isHost, value);
        }

        private bool isTransferring;
        public bool IsTransferring
        {
            get => isTransferring;
            set => Set(ref isTransferring, value);
        }

        private string transferStatusText = string.Empty;
        public string TransferStatusText
        {
            get => transferStatusText;
            set => Set(ref transferStatusText, value);
        }

        private double transferProgress;
        public double TransferProgress
        {
            get => transferProgress;
            set => Set(ref transferProgress, value);
        }

        public UserPermission GlobalDefaultPermission { get; } = new();
        private UserPermission currentUserPermission = new();
        public UserPermission CurrentUserPermission
        {
            get => currentUserPermission;
            set => Set(ref currentUserPermission, value);
        }

        public bool IsConnected
        {
            get => isConnected;
            private set => Set(ref isConnected, value);
        }

        public CollaborationSession(Scenes scenes)
        {
            Scenes = scenes;
            sessionClient = new SessionClient(new WebsocketProvider());
            eventDispatcher = new ClientEventDispatcher();
            adornerManager = new AdornerManager();
            fileTransferManager = new FileTransferManager
            {
                GetPeerCount = () => Participants.Count(p => p.UserId != LocalUserId)
            };
            eventSender = new EditEventSender(sessionClient, fileTransferManager, () => LocalUserId)
            {
                IsItemAlive = item => Scenes?.Timelines.Any(timeline => timeline.Items.Contains(item)) ?? false
            };
            timelineSyncManager = new TimelineSyncManager(eventSender, () => isApplyingRemoteEvent, IsItemEditableLocally);
            characterShareManager = new CharacterShareManager(
                sessionClient,
                fileTransferManager,
                () => LocalUserId,
                userId => Participants.FirstOrDefault(p => p.UserId == userId)?.UserName ?? string.Empty,
                () => activeViewModel,
                ExecuteRemoteAction);

            fileTransferManager.TransferCompleted += (transferId, path) => FileTransferCompleted?.Invoke(transferId, path);
            fileTransferManager.SentFilesChanged += () => SentFilesChanged?.Invoke();
            fileTransferManager.TransferSummaryChanged += OnTransferSummaryChanged;

            LocalUserId = sessionClient.LocalUserId;

            ResourceAvailabilityChecker.MissingReported = SendMissingResource;

            sessionClient.EventReceived += HandleEventReceived;
            sessionClient.Disconnected += HandleDisconnected;
            sessionClient.RoomNotFound += HandleRoomNotFound;
            sessionClient.UpdateAvailable += HandleUpdateAvailable;
            sessionClient.PeerDisconnected += HandlePeerDisconnected;
            sessionClient.ConnectionStateChanged += HandleConnectionStateChanged;

            CreateRoomCommand = new ActionCommand(_ => !IsConnected && !isJoining, async _ => await CreateRoomAsync());
            JoinRoomCommand = new ActionCommand(_ => !IsConnected && !isJoining && !string.IsNullOrWhiteSpace(InputRoomId), async _ => await JoinRoomAsync());
            CopyRoomIdCommand = new ActionCommand(_ => !string.IsNullOrEmpty(RoomId), _ => Clipboard.SetText(RoomId));
            CopyInviteLinkCommand = new ActionCommand(_ => !string.IsNullOrEmpty(RoomId), _ => Clipboard.SetText($"https://multi-user-edit.dolphin-discord-js.workers.dev/?roomId={RoomId}"));
            JoinFromClipboardCommand = new ActionCommand(_ => !IsConnected && !isJoining, async _ =>
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    var extracted = MultiUserEditViewModel.ExtractRoomId(text);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        InputRoomId = extracted;
                        await JoinRoomAsync();
                    }
                }
            });
            RegisterProtocolCommand = new ActionCommand(_ => true, _ =>
            {
                if (ProtocolRegister.RegisterCustomProtocol())
                {
                    IsProtocolRegistered = true;
                    MessageBox.Show($"Webディープリンク (ymm4-multi-user-edit://) をWindowsに登録しました。", "登録完了", MessageBoxButton.OK);
                }
            });
            UnregisterProtocolCommand = new ActionCommand(_ => true, _ =>
            {
                if (ProtocolRegister.UnregisterCustomProtocol())
                {
                    IsProtocolRegistered = false;
                    MessageBox.Show($"Webディープリンク (ymm4-multi-user-edit://) の登録を解除しました。", "解除完了", MessageBoxButton.OK);
                }
            });
            DisconnectCommand = new ActionCommand(_ => IsConnected || isJoining, async _ => await StopNetworkAsync());
            SyncNowCommand = new ActionCommand(_ => IsConnected && !isJoining, _ => SyncNow());
            KickUserCommand = new ActionCommand(
                param => IsHost && param is Guid targetId && targetId != LocalUserId,
                async param =>
                {
                    if (param is Guid targetUserId)
                    {
                        var target = Participants.FirstOrDefault(p => p.UserId == targetUserId);
                        var name = target?.UserName ?? "指定ユーザー";
                        var result = MessageBox.Show($"{name} をルームからキックしてもよろしいですか？", "キックの確認", MessageBoxButton.OKCancel);
                        if (result == MessageBoxResult.OK)
                        {
                            await KickUserAsync(targetUserId);
                        }
                    }
                });

            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
            Application.Current?.Exit += Application_Exit;

            ProtocolRegister.RegisterCustomProtocol();
            IsProtocolRegistered = ProtocolRegister.IsRegistered();
            CheckCommandLineArgsForDeepLink();

            presenceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            presenceTimer.Tick += PresenceTimer_Tick;
            presenceTimer.Start();
        }

        private void CurrentDomain_ProcessExit(object? sender, EventArgs e)
        {
            OnApplicationTerminating();
        }

        private void Application_Exit(object? sender, ExitEventArgs e)
        {
            OnApplicationTerminating();
        }

        private static readonly TimeSpan TerminationSendTimeout = TimeSpan.FromSeconds(2);

        private Task SendCloseRoomAsync() => sessionClient.SendAsync(null, new { type = "close_room" });

        private void OnApplicationTerminating()
        {
            if (IsConnected && !disposed)
            {
                try
                {
                    var evt = new UserLeftEvent(LocalUserId, IsHost)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    };
                    sessionClient.SendAsync(null, evt).Wait(TerminationSendTimeout);
                    if (IsHost) SendCloseRoomAsync().Wait(TerminationSendTimeout);
                    _ = sessionClient.StopAsync();
                }
                catch { }
            }
        }

        private void CheckCommandLineArgsForDeepLink()
        {
            try
            {
                var targetArg = GetDeepLinkTargetArgument();
                if (string.IsNullOrEmpty(targetArg)) return;

                var extracted = MultiUserEditViewModel.ExtractRoomId(targetArg);
                if (string.IsNullOrEmpty(extracted)) return;

                InputRoomId = extracted;
                if (!IsConnected)
                {
                    Application.Current?.Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(500);
                        if (!IsConnected && !string.IsNullOrEmpty(InputRoomId))
                        {
                            await JoinRoomAsync();
                        }
                    }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] DeepLink arg parse failed: {ex.Message}");
            }
        }

        private static string? GetDeepLinkTargetArgument()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("ymm4-multi-user-edit://", StringComparison.OrdinalIgnoreCase))
                {
                    return arg;
                }
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "deeplink.txt");
            if (File.Exists(tempPath))
            {
                try
                {
                    var content = File.ReadAllText(tempPath).Trim();
                    File.Delete(tempPath);
                    if (!string.IsNullOrEmpty(content))
                    {
                        return content;
                    }
                }
                catch { }
            }

            return null;
        }

        private int transferDisplayGeneration;

        public ObservableCollection<TransferItemInfo> ActiveTransfers { get; } = [];

        private void UpdateActiveTransfers(TransferSummary summary)
        {
            ActiveTransfers.Clear();
            foreach (var item in summary.Items) ActiveTransfers.Add(item);
            OnPropertyChanged(nameof(ActiveTransfers));
        }

        private void OnTransferSummaryChanged(TransferSummary summary)
        {
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                UpdateActiveTransfers(summary);

                if (!summary.IsActive)
                {
                    if (!IsTransferring) return;

                    var myGeneration = Volatile.Read(ref transferDisplayGeneration);
                    TransferProgress = 100.0;
                    await Task.Delay(1000);
                    if (Volatile.Read(ref transferDisplayGeneration) != myGeneration) return;

                    IsTransferring = false;
                    TransferProgress = 0;
                    TransferStatusText = string.Empty;
                    return;
                }

                Interlocked.Increment(ref transferDisplayGeneration);
                IsTransferring = true;
                TransferStatusText = summary.DisplayText;
                TransferProgress = summary.OverallProgress;
            });
        }

        private static readonly TimeSpan AwayThreshold = TimeSpan.FromMinutes(3);

        private void PresenceTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.Now;
            foreach (var p in Participants)
            {
                if (p.UserId == LocalUserId)
                {
                    p.LastActivity = sessionClient.LastSentAt;
                    p.Status = now - p.LastActivity > AwayThreshold ? UserStatus.Away : UserStatus.Active;
                    continue;
                }

                if (p.Status != UserStatus.Disconnected && now - p.LastActivity > AwayThreshold)
                    p.Status = UserStatus.Away;
            }

            var staleOperations = OperatingItems
                .Where(kvp => DateTime.UtcNow - kvp.Value.LastActivity > TimeSpan.FromSeconds(10))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var itemId in staleOperations) OperatingItems.Remove(itemId);

            var expired = lockedItemsReceivedAt
                .Where(kvp => DateTime.UtcNow - kvp.Value > TimeSpan.FromMinutes(5))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var itemId in expired)
            {
                LockedItems.Remove(itemId);
                lockedItemsReceivedAt.Remove(itemId);
            }
        }

        public void SetTimelineViewReference(YukkuriMovieMaker.Views.TimelineView timelineView)
        {
            adornerManager.SetTimelineViewReference(timelineView);
        }

        public void SetTimelineToolInfo(TimelineToolInfo info, MultiUserEditViewModel viewModel)
        {
            undoRedoManager?.UndoRedoCommandCreated -= OnUndoRedoCommandCreated;
            if (Scenes is not null)
            {
                UnsubscribeScenesEvents();
                foreach (var timeline in Scenes.Timelines) timelineSyncManager.UnregisterTimeline(timeline);
            }

            undoRedoManager = info.UndoRedoManager;
            if (info.Scenes != null)
            {
                Scenes = info.Scenes;
            }
            FirstOrDefaultTimeline = Scenes?.Timelines.FirstOrDefault(t => t.ID == info.Timeline?.ID) ?? Scenes?.Timelines.FirstOrDefault();

            undoRedoManager?.UndoRedoCommandCreated += OnUndoRedoCommandCreated;

            if (Scenes is not null)
            {
                SubscribeScenesEvents(viewModel);
                foreach (var timeline in Scenes.Timelines) timelineSyncManager.RegisterTimeline(timeline, viewModel);
                adornerManager.AttachAdorner(viewModel);
            }
            else
            {
                adornerManager.DetachAdorner();
            }

            if (IsConnected && FirstOrDefaultTimeline != null)
                SendCursorMovedAsync(FirstOrDefaultTimeline.CurrentFrame);

            RefreshCommandStates();
        }

        private MultiUserEditViewModel? activeViewModel;

        private void SubscribeScenesEvents(MultiUserEditViewModel viewModel)
        {
            if (activeViewModel != null && !ReferenceEquals(activeViewModel, viewModel))
            {
                activeViewModel.DetachFromSession();
            }

            activeViewModel = viewModel;
            if (Scenes is INotifyPropertyChanged npc)
            {
                npc.PropertyChanged -= Scenes_PropertyChanged;
                npc.PropertyChanged += Scenes_PropertyChanged;
            }
            scenesSnapshot = Scenes?.Timelines.ToList() ?? [];
        }

        private void UnsubscribeScenesEvents()
        {
            if (Scenes is INotifyPropertyChanged npc)
            {
                npc.PropertyChanged -= Scenes_PropertyChanged;
            }
            scenesSnapshot.Clear();
        }

        private void Scenes_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (isApplyingRemoteEvent || !IsConnected || Scenes == null) return;
            if (e.PropertyName != nameof(Scenes.Timelines)) return;

            var currentTimelines = Scenes.Timelines.ToList();

            if (!CurrentUserPermission.CanManageScenes && currentTimelines.Count > scenesSnapshot.Count)
            {
                var newlyAdded = currentTimelines.Except(scenesSnapshot).ToList();
                var previous = isApplyingRemoteEvent;
                isApplyingRemoteEvent = true;
                try
                {
                    foreach (var t in newlyAdded)
                    {
                        Scenes.DeleteScene(t);
                    }
                }
                finally { isApplyingRemoteEvent = previous; }
                return;
            }

            if (activeViewModel != null)
            {
                for (int i = 0; i < currentTimelines.Count; i++)
                {
                    var t = currentTimelines[i];
                    if (!scenesSnapshot.Contains(t))
                    {
                        timelineSyncManager.RegisterTimeline(t, activeViewModel);
                        SendSceneAddedAsync(i, t.Name);
                    }
                }
            }

            for (int i = 0; i < scenesSnapshot.Count; i++)
            {
                var oldT = scenesSnapshot[i];
                if (!currentTimelines.Contains(oldT))
                {
                    timelineSyncManager.UnregisterTimeline(oldT);
                    SendSceneRemovedAsync(i);
                }
            }

            scenesSnapshot = currentTimelines;
        }

        public void ExecuteRemoteAction(Action action)
        {
            var previous = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            using var undoScope = UndoRecordSuppressor.Suppress(undoRedoManager);
            try
            {
                action();
            }
            finally
            {
                isApplyingRemoteEvent = previous;
            }
        }

        private async void SendSceneAddedAsync(int index, string name)
        {
            if (!CurrentUserPermission.CanManageScenes) return;
            await eventSender.SendSceneAddedAsync(index, name);
        }

        private async void SendSceneRemovedAsync(int index)
        {
            if (!CurrentUserPermission.CanManageScenes) return;
            await eventSender.SendSceneRemovedAsync(index);
        }

        public async void UpdateGlobalPermission(UserPermission newPerm)
        {
            GlobalDefaultPermission.Level = newPerm.Level;
            GlobalDefaultPermission.CanAddItems = newPerm.CanAddItems;
            GlobalDefaultPermission.CanMoveItems = newPerm.CanMoveItems;
            GlobalDefaultPermission.CanDeleteItems = newPerm.CanDeleteItems;
            GlobalDefaultPermission.CanEditProperties = newPerm.CanEditProperties;
            GlobalDefaultPermission.CanManageScenes = newPerm.CanManageScenes;

            foreach (var p in Participants)
            {
                p.Permission = newPerm;
            }

            var evt = new PermissionUpdatedEvent(null, newPerm)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            await sessionClient.SendAsync(null, evt);
        }

        public async void UpdateUserPermission(Guid targetUserId, UserPermission newPerm)
        {
            var p = Participants.FirstOrDefault(x => x.UserId == targetUserId);
            p?.Permission = newPerm;

            var evt = new PermissionUpdatedEvent(targetUserId, newPerm)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            await sessionClient.SendAsync(null, evt);
        }

        internal void HandlePermissionUpdated(PermissionUpdatedEvent evt)
        {
            if (evt.TargetUserId == null || evt.TargetUserId == Guid.Empty)
            {
                GlobalDefaultPermission.Level = evt.Permission.Level;
                GlobalDefaultPermission.CanAddItems = evt.Permission.CanAddItems;
                GlobalDefaultPermission.CanMoveItems = evt.Permission.CanMoveItems;
                GlobalDefaultPermission.CanDeleteItems = evt.Permission.CanDeleteItems;
                GlobalDefaultPermission.CanEditProperties = evt.Permission.CanEditProperties;
                GlobalDefaultPermission.CanManageScenes = evt.Permission.CanManageScenes;

                if (!IsHost)
                {
                    CurrentUserPermission = evt.Permission;
                }
            }
            else if (evt.TargetUserId == LocalUserId)
            {
                CurrentUserPermission = evt.Permission;
            }

            var target = Participants.FirstOrDefault(x => x.UserId == evt.TargetUserId);
            target?.Permission = evt.Permission;
        }

        internal void HandleSceneAdded(SceneAddedEvent evt)
        {
            if (Scenes == null) return;

            try
            {
                if (evt.Index <= Scenes.Timelines.Count)
                {
                    var videoInfo = FirstOrDefaultTimeline?.VideoInfo ?? new VideoInfo();
                    var verticalLine = FirstOrDefaultTimeline?.VerticalLine ?? new TimelineVerticalLine();
                    Scenes.CreateScene(evt.Index, videoInfo, verticalLine);
                    var timeline = Scenes.Timelines.ElementAtOrDefault(evt.Index);
                    if (timeline != null && !string.IsNullOrEmpty(evt.Name))
                        timeline.Name = evt.Name;

                    scenesSnapshot = [.. Scenes.Timelines];
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] HandleSceneAdded failed: {ex.Message}");
            }
        }

        internal void HandleSceneRemoved(SceneRemovedEvent evt)
        {
            if (Scenes == null) return;

            try
            {
                var timeline = Scenes.Timelines.ElementAtOrDefault(evt.Index);
                if (timeline != null)
                {
                    Scenes.DeleteScene(timeline);
                    scenesSnapshot = [.. Scenes.Timelines];
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] HandleSceneRemoved failed: {ex.Message}");
            }
        }

        public void RefreshCommandStates()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(RefreshCommandStates);
                return;
            }

            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsHost));
            OnPropertyChanged(nameof(RoomId));
            OnPropertyChanged(nameof(InputRoomId));
            (CreateRoomCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (JoinRoomCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (CopyRoomIdCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (CopyInviteLinkCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (JoinFromClipboardCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (SyncNowCommand as ActionCommand)?.RaiseCanExecuteChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private static string SyncFromConfirmMessage(string sourceName) =>
            $"現在のタイムライン上のアイテムをすべて削除して、{sourceName} の状態と同期します。\nよろしいですか？";

        public void ApplySyncScenes(OnlineScenes onlineScenes, Guid ownerId, bool isManual)
        {
            if (onlineScenes == null || Scenes == null) return;

            if (isManual)
            {
                var requestedByMe = pendingManualSyncSource == ownerId;
                if (requestedByMe) pendingManualSyncSource = null;

                if (!requestedByMe && Scenes.Timelines.Any(t => t.Items.Count > 0))
                {
                    var sourceName = GetParticipantName(ownerId);
                    var accepted = MessageBox.Show(
                        $"{sourceName} がデータの同期を実行しました。\n\n" + SyncFromConfirmMessage(sourceName),
                        "データの同期",
                        MessageBoxButton.OKCancel) == MessageBoxResult.OK;

                    if (!accepted) return;
                }
            }
            else if (!awaitingInitialSync)
            {
                return;
            }
            else
            {
                awaitingInitialSync = false;
            }

            if (!isManual && Scenes.Timelines.Any(t => t.Items.Count > 0))
            {
                var result = MessageBox.Show(
                    "ルームに参加するため、現在のタイムライン上のアイテムをすべて削除してホストの状態と同期します。\n削除して同期を開始してもよろしいですか？",
                    "初期同期の確認",
                    MessageBoxButton.OKCancel);

                if (result != MessageBoxResult.OK)
                {
                    Application.Current?.Dispatcher.InvokeAsync(async () => await StopNetworkAsync());
                    return;
                }
            }

            eventSender.ClearAllBaselines();

            var previousApplying = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            using var undoScope = UndoRecordSuppressor.Suppress(undoRedoManager);
            try
            {
                for (int i = 0; i < onlineScenes.Timelines.Count; i++)
                {
                    var onlineTimeline = onlineScenes.Timelines[i];
                    var timeline = Scenes.Timelines.FirstOrDefault(t => t.ID == onlineTimeline.Id)
                                ?? Scenes.Timelines.ElementAtOrDefault(i);

                    if (timeline == null)
                    {
                        try
                        {
                            var videoInfo = FirstOrDefaultTimeline?.VideoInfo ?? new VideoInfo();
                            var verticalLine = FirstOrDefaultTimeline?.VerticalLine ?? new TimelineVerticalLine();
                            Scenes.CreateScene(i, videoInfo, verticalLine);
                            timeline = Scenes.Timelines.ElementAtOrDefault(i);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[MultiUserEdit] CreateScene failed: {ex.Message}");
                        }
                    }

                    if (timeline == null) continue;

                    timeline.Name = onlineTimeline.Name;

                    if (timeline.VideoInfo != null)
                    {
                        VideoInfoSerializer.Apply(timeline.VideoInfo, onlineTimeline.Width, onlineTimeline.Height,
                            onlineTimeline.FPS, onlineTimeline.Hz, onlineTimeline.BackgroundColor);
                    }

                    if (timeline.Items.Count > 0)
                        timeline.DeleteItems([.. timeline.Items]);

                    foreach (var onlineItem in onlineTimeline.Items)
                    {
                        AddSyncedItem(timeline, onlineItem, ownerId);
                    }
                }

                scenesSnapshot = [.. Scenes.Timelines];
            }
            finally
            {
                isApplyingRemoteEvent = previousApplying;
                if (activeViewModel != null)
                {
                    adornerManager.AttachAdorner(activeViewModel);
                }
            }
        }

        private void AddSyncedItem(Timeline timeline, OnlineItem onlineItem, Guid ownerId) =>
            AddSyncedItem(timeline, onlineItem, ownerId, canWait: true);

        private void AddSyncedItem(Timeline timeline, OnlineItem onlineItem, Guid ownerId, bool canWait)
        {
            var itemType = ItemTypeResolver.Resolve(onlineItem.ItemTypeName);
            if (itemType == null)
            {
                ResourceAvailabilityChecker.Notify(onlineItem.ItemId, ownerId, GetParticipantName(ownerId),
                    onlineItem.ItemJson, onlineItem.ItemTypeName);
                return;
            }

            try
            {
                var characterName = MediaFileResolver.GetCharacterNameFromJson(onlineItem.ItemJson);
                if (!string.IsNullOrEmpty(characterName) && !CharacterShareManager.IsDecided(characterName))
                {
                    RequestCharacter(characterName, ownerId, () => AddSyncedItem(timeline, onlineItem, ownerId));
                    return;
                }

                if (MediaFileResolver.RequiresRealMediaContainer(itemType))
                {
                    var pending = MediaFileResolver.GetMissingFileNames(onlineItem.MediaFileNames);
                    if (pending.Count > 0)
                    {
                        if (canWait && activeViewModel != null)
                        {
                            TransferWaiter.WhenFilesReady(activeViewModel, pending,
                                () => AddSyncedItem(timeline, onlineItem, ownerId, canWait: false),
                                TransferWaiter.DefaultTimeout);
                            return;
                        }

                        ErrorNotifier.NotifyOnce(
                            "動画・音声ファイルを受信できませんでした",
                            $"{GetParticipantName(ownerId)} のアイテムのファイルが届かなかったため、アイテムを追加できませんでした。\n\n"
                            + string.Join("\n", pending.Take(5).Select(name => "・" + name))
                            + "\n\n「データを同期する」を実行すると、もう一度受信できます。");
                        return;
                    }
                }

                var itemJson = MediaFileResolver.ResolveJsonFileReferences(onlineItem.ItemJson, itemType, onlineItem.MediaFileNames);

                if (JsonConvert.DeserializeObject(itemJson, itemType, ItemSerializerOptions.Default) is not IItem item) return;

                CharacterResolver.TryResolveCharacter(item);

                ItemIdManager.RegisterId(item, onlineItem.ItemId);
                timeline.TryAddItems([item], onlineItem.Frame, onlineItem.Layer, false);

                var missingFiles = MediaFileResolver.GetMissingFileNames(
                    onlineItem.MediaFileNames, TachieFileResolver.GetBaseDirectory(item));
                if (missingFiles.Count > 0 && activeViewModel != null)
                {
                    TransferWaiter.WhenFilesReady(activeViewModel, missingFiles, () =>
                        JsonConvert.PopulateObject(
                            MediaFileResolver.ResolveJsonFileReferences(onlineItem.ItemJson, itemType, onlineItem.MediaFileNames),
                            item,
                            ItemSerializerOptions.Default),
                        TransferWaiter.DefaultTimeout);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Sync item failed ({onlineItem.ItemTypeName}): {ex.Message}");
            }
        }

        private async Task CreateRoomAsync()
        {
            if (!await EnsureLatestPluginAsync()) return;

            RoomId = Guid.NewGuid().ToString();
            hostKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            localUserRole = UserRole.Host;
            IsHost = true;
            CurrentUserPermission = UserPermission.CreateFromLevel(PermissionLevel.Full);
            await ConnectNetworkAsync(RoomId);
        }

        private async Task JoinRoomAsync()
        {
            if (!await EnsureLatestPluginAsync()) return;

            RoomId = InputRoomId;
            localUserRole = UserRole.Guest;
            IsHost = false;
            await ConnectNetworkAsync(RoomId);
        }

        private string? hostKey;

        private static async Task<bool> EnsureLatestPluginAsync()
        {
            var checker = UpdateChecker.Instance;
            if (!await checker.IsOutdatedAsync(TimeSpan.FromSeconds(3))) return true;

            PromptUpdate($"新しいバージョン ({checker.LatestVersion}) が公開されています。\n共同編集は最新のプラグイン同士でのみ利用できます。");
            return false;
        }

        private static void PromptUpdate(string reason)
        {
            var result = MessageBox.Show(
                $"{reason}\n\n更新ページを開きますか？",
                "プラグインの更新が必要です",
                MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes) UpdateChecker.Instance.OpenUpdatePage();
        }

        private TaskCompletionSource<bool>? guestRoomValidation;
        private bool isJoining;

        private int connectGeneration;

        private async Task ConnectNetworkAsync(string targetRoomId)
        {
            var myGeneration = Interlocked.Increment(ref connectGeneration);
            bool IsCurrent() => Volatile.Read(ref connectGeneration) == myGeneration;

            isJoining = true;
            RefreshCommandStates();
            try
            {
                if (localUserRole == UserRole.Guest)
                {
                    guestRoomValidation = new TaskCompletionSource<bool>();
                }

                await sessionClient.StartAsync(targetRoomId, localUserRole == UserRole.Host, hostKey);

                if (!IsCurrent()) return;

                if (localUserRole == UserRole.Guest)
                {
                    var validation = guestRoomValidation!;
                    var winner = await Task.WhenAny(validation.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
                    var roomIsValid = winner != validation.Task || validation.Task.Result;
                    guestRoomValidation = null;

                    if (!IsCurrent() || !roomIsValid || !sessionClient.IsConnected) return;

                    IsConnected = true;
                    RefreshCommandStates();
                }

                Participants.Clear();
                eventSender.ClearAllBaselines();
                HasDivergenceWarning = false;
                ResourceAvailabilityChecker.Reset();
                AddParticipantSorted(new Participant
                {
                    UserId = LocalUserId,
                    ProfileId = LocalProfileId,
                    UserName = UserName,
                    Description = UserDescription,
                    Role = localUserRole,
                    Permission = CurrentUserPermission,
                    LastActivity = DateTime.Now,
                    JoinedAt = DateTime.Now,
                    Status = UserStatus.Active,
                    ThemeColor = ParticipantColorGenerator.Generate(LocalUserId)
                });

                var presenceEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, false, LocalProfileId, UserDescription, UpdateChecker.Instance.CurrentVersion)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, presenceEvt);

                if (!IsCurrent()) return;

                if (localUserRole == UserRole.Guest)
                {
                    awaitingInitialSync = true;
                    var syncRequest = new SyncRequestEvent
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    };
                    await sessionClient.SendAsync(null, syncRequest);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] start failed: {ex.Message}");
            }
            finally
            {
                if (IsCurrent())
                {
                    isJoining = false;
                    RefreshCommandStates();
                }
            }
        }

        public async Task StopNetworkAsync()
        {
            try
            {
                if (IsConnected)
                {
                    await sessionClient.SendAsync(null, new UserLeftEvent(LocalUserId, IsHost)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    });

                    if (IsHost) await SendCloseRoomAsync();
                }
                await sessionClient.StopAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] StopNetworkAsync exception: {ex.Message}");
            }
            finally
            {
                ResetNetworkState();
            }
        }

        private void ResetNetworkState()
        {
            Interlocked.Increment(ref connectGeneration);

            void ClearState()
            {
                IsConnected = false;
                isJoining = false;
                watchedHostId = Guid.Empty;
                awaitingInitialSync = false;
                pendingManualSyncSource = null;
                IsHost = false;
                HasDivergenceWarning = false;
                hostKey = null;
                RoomId = string.Empty;
                InputRoomId = string.Empty;
                Participants.Clear();
                locallyLockedItems.Clear();
                lockedItemsReceivedAt.Clear();
                LockedItems.Clear();
                OperatingItems.Clear();
                fileTransferManager.CancelAll();
                characterShareManager.Reset();
                RefreshCommandStates();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.InvokeAsync(ClearState);
            else
                ClearState();
        }

        private void HandleEventReceived(object? sender, EditEvent editEvent)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (editEvent is FileTransferStartEvent or FileChunkEvent)
            {
                if (editEvent.ExecutorId == LocalUserId) return;

                if (editEvent is FileTransferStartEvent start) fileTransferManager.HandleTransferStart(start);
                else if (editEvent is FileChunkEvent chunk) fileTransferManager.HandleChunk(chunk);

                var executorId = editEvent.ExecutorId;
                dispatcher.InvokeAsync(() => UpdateParticipantActivity(executorId));
                return;
            }

            void Process()
            {
                if (editEvent.ExecutorId == LocalUserId) return;

                UpdateParticipantActivity(editEvent.ExecutorId);
                DispatchEvent(editEvent);
                NoteRemoteOperation(editEvent);
            }

            if (dispatcher.CheckAccess())
                Process();
            else
                dispatcher.Invoke(Process);
        }

        private void UpdateParticipantActivity(Guid executorId)
        {
            if (executorId == Guid.Empty || executorId == LocalUserId) return;

            NotePeerAlive(executorId);

            var p = EnsureParticipant(executorId);
            p.Status = UserStatus.Active;
            p.LastActivity = DateTime.Now;
        }

        private Participant EnsureParticipant(Guid userId)
        {
            var p = Participants.FirstOrDefault(x => x.UserId == userId);
            if (p != null)
            {
                p.LastActivity = DateTime.Now;
                p.Status = UserStatus.Active;
                return p;
            }

            p = new Participant
            {
                UserId = userId,
                UserName = $"User {userId.ToString()[..8]}",
                Role = UserRole.Guest,
                LastActivity = DateTime.Now,
                JoinedAt = DateTime.Now,
                ThemeColor = ParticipantColorGenerator.Generate(userId),
                Status = UserStatus.Active
            };

            AddParticipantSorted(p);
            return p;
        }

        internal void HandlePresenceEvent(PresenceEvent evt)
        {
            var p = Participants.FirstOrDefault(x => x.UserId == evt.UserId);
            if (p == null)
            {
                p = new Participant
                {
                    UserId = evt.UserId,
                    ProfileId = evt.ProfileId,
                    UserName = evt.UserName,
                    Description = ProfileText.NormalizeDescription(evt.Description),
                    Role = evt.Role,
                    LastActivity = DateTime.Now,
                    JoinedAt = DateTime.Now,
                    ThemeColor = ParticipantColorGenerator.Generate(evt.UserId),
                    Status = UserStatus.Active
                };
                AddParticipantSorted(p);
            }
            else
            {
                p.UserName = evt.UserName;
                p.Description = ProfileText.NormalizeDescription(evt.Description);
                p.Role = evt.Role;
                p.LastActivity = DateTime.Now;
                p.Status = UserStatus.Active;
                if (evt.ProfileId != Guid.Empty) p.ProfileId = evt.ProfileId;
                MoveHostToTopIfNeeded(p);
            }

            if (!IsHost && evt.Role == UserRole.Host && !IsCompatibleVersion(evt.Version))
            {
                HandleHostVersionMismatch(evt.Version);
                return;
            }

            if (!evt.IsReply)
            {
                var replyEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, true, LocalProfileId, UserDescription, UpdateChecker.Instance.CurrentVersion)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                _ = sessionClient.SendAsync(null, replyEvt);

                if (FirstOrDefaultTimeline != null)
                    SendCursorMovedAsync(FirstOrDefaultTimeline.CurrentFrame);
            }
        }

        private bool versionMismatchHandled;

        private static bool IsCompatibleVersion(string peerVersion) =>
            Version.TryParse(peerVersion, out var peer)
            && Version.TryParse(UpdateChecker.Instance.CurrentVersion, out var mine)
            && peer == mine;

        private void HandleHostVersionMismatch(string hostVersion)
        {
            if (versionMismatchHandled) return;
            versionMismatchHandled = true;

            var hostIsNewer = !Version.TryParse(hostVersion, out var host)
                || !Version.TryParse(UpdateChecker.Instance.CurrentVersion, out var mine)
                || host > mine;
            var shownHostVersion = string.IsNullOrEmpty(hostVersion) ? "不明" : hostVersion;

            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                await StopNetworkAsync();
                versionMismatchHandled = false;

                if (hostIsNewer)
                {
                    PromptUpdate($"ホストのプラグイン ({shownHostVersion}) より古いバージョン ({UpdateChecker.Instance.CurrentVersion}) のため、接続を切断しました。");
                }
                else
                {
                    MessageBox.Show(
                        $"ホストのプラグイン ({shownHostVersion}) が古いため、接続を切断しました。\nホストにプラグインの更新を依頼してください。",
                        "接続エラー",
                        MessageBoxButton.OK);
                }
            });
        }

        internal void HandleSyncRequestEvent(SyncRequestEvent evt)
        {
            var isAddressedToMe = evt.SourceUserId == LocalUserId
                || (evt.SourceUserId == null && localUserRole == UserRole.Host);
            if (!isAddressedToMe) return;

            SendSyncScenes(evt.ExecutorId.ToString(), evt.IsManual);
        }

        private void SendSyncScenes(string? targetId, bool isManual)
        {
            if (Scenes == null) return;

            var onlineScenes = new OnlineScenes();
            var filesToTransfer = new List<(SharedFile File, string? CharacterName)>();
            foreach (var timeline in Scenes.Timelines)
            {
                var onlineTimeline = new OnlineTimeline
                {
                    Id = timeline.ID,
                    Name = timeline.Name,
                    Length = timeline.Length,
                    Width = timeline.VideoInfo?.Width ?? 0,
                    Height = timeline.VideoInfo?.Height ?? 0,
                    FPS = timeline.VideoInfo?.FPS ?? 0,
                    Hz = timeline.VideoInfo?.Hz ?? 0,
                    BackgroundColor = timeline.VideoInfo is { } info ? VideoInfoSerializer.ToText(info.BackgroundColor) : null
                };
                foreach (var item in timeline.Items)
                {
                    var itemType = item.GetType();
                    var (itemJson, files) = MediaFileResolver.SerializeForSync(item, itemType);
                    onlineTimeline.Items.Add(new OnlineItem
                    {
                        ItemId = ItemIdManager.GetOrCreateId(item),
                        ItemTypeName = ItemTypeResolver.GetTypeName(itemType),
                        ItemJson = itemJson,
                        MediaFileNames = files.Count > 0 ? [.. files.Where(file => file.ShouldTransfer).Select(file => file.Name)] : null,
                        Frame = item.Frame,
                        Layer = item.Layer
                    });

                    var characterName = CharacterResolver.GetCharacter(item)?.Name;
                    filesToTransfer.AddRange(files.Where(file => file.ShouldTransfer).Select(file => (file, characterName)));
                }
                onlineScenes.Timelines.Add(onlineTimeline);
            }

            var syncEvent = new SyncScenesEvent(onlineScenes, isManual)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            _ = sessionClient.SendAsync(targetId, syncEvent);

            fileTransferManager.ForgetAnnouncedFiles();

            foreach (var (file, characterName) in filesToTransfer.DistinctBy(entry => entry.File.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                if (fileTransferManager.IsSendDenied(file.FullPath)) continue;

                fileTransferManager.MarkSendAllowed(file.FullPath);
                _ = fileTransferManager.TransferAsync(file, characterName, sessionClient, LocalUserId);
            }
        }

        private void DispatchEvent(EditEvent editEvent)
        {
            if (activeViewModel == null) return;

            var previous = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            using var undoScope = UndoRecordSuppressor.Suppress(undoRedoManager);
            try
            {
                eventDispatcher.Dispatch(editEvent, activeViewModel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] Dispatch failed: {ex.Message}");
            }
            finally
            {
                isApplyingRemoteEvent = previous;
            }
        }

        private static readonly TimeSpan CursorEchoWindow = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan[] ReconnectDelays =
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30),
        ];

        private bool isReconnecting;

        private void HandleDisconnected()
        {
            Debug.WriteLine("[MultiUserEdit] Disconnected by server");

            if (disposed || isReconnecting || string.IsNullOrEmpty(RoomId))
            {
                ResetNetworkState();
                return;
            }

            _ = ReconnectAsync(RoomId, localUserRole);
        }

        private async Task ReconnectAsync(string targetRoomId, UserRole role)
        {
            isReconnecting = true;
            try
            {
                for (var attempt = 0; attempt < ReconnectDelays.Length; attempt++)
                {
                    await Task.Delay(ReconnectDelays[attempt]);

                    if (disposed || sessionClient.IsConnected || RoomId != targetRoomId) return;

                    try
                    {
                        await sessionClient.StartAsync(targetRoomId, role == UserRole.Host, hostKey);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!sessionClient.IsConnected) continue;

                    BroadcastLocalPresence();
                    CheckStateAfterReconnect(role);
                    Debug.WriteLine($"[MultiUserEdit] Reconnected (attempt {attempt + 1})");
                    return;
                }

                ErrorNotifier.NotifyOnce(
                    "接続が切断されました",
                    "共同編集サーバーとの接続が切れ、繋ぎ直せませんでした。\n" +
                    "通信環境を確認して、もう一度ルームに参加してください。");
                ResetNetworkState();
            }
            finally
            {
                isReconnecting = false;
            }
        }

        private void CheckStateAfterReconnect(UserRole role)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (!IsConnected) return;

                if (role == UserRole.Host)
                {
                    var digest = ComputeStateDigest();
                    if (digest == null) return;

                    _ = sessionClient.SendAsync(null, new StateDigestEvent(digest)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    });
                    return;
                }

                var host = Participants.FirstOrDefault(p => p.Role == UserRole.Host && p.UserId != LocalUserId);
                _ = sessionClient.SendAsync(host?.UserId.ToString(), new StateDigestRequestEvent
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                });
            });
        }

        private void SendMissingResource(Guid targetUserId, string[] fonts, string[] plugins)
        {
            if (!IsConnected || targetUserId == LocalUserId) return;
            if (fonts.Length == 0 && plugins.Length == 0) return;

            _ = sessionClient.SendAsync(targetUserId.ToString(), new MissingResourceEvent(fonts, plugins)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
        }

        internal void HandleMissingResource(MissingResourceEvent evt)
        {
            ResourceAvailabilityChecker.NotifyReportedByPeer(
                GetParticipantName(evt.ExecutorId), evt.Fonts, evt.Plugins);
        }

        internal void HandleStateDigestRequest(StateDigestRequestEvent evt)
        {
            if (!IsConnected || !IsHost) return;

            var digest = ComputeStateDigest();
            if (digest == null) return;

            _ = sessionClient.SendAsync(evt.ExecutorId.ToString(), new StateDigestEvent(digest)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
        }

        private bool hasDivergenceWarning;
        public bool HasDivergenceWarning
        {
            get => hasDivergenceWarning;
            private set => Set(ref hasDivergenceWarning, value);
        }

        private string divergenceWarningText = string.Empty;
        public string DivergenceWarningText
        {
            get => divergenceWarningText;
            private set => Set(ref divergenceWarningText, value);
        }

        public void DismissDivergenceWarning() => HasDivergenceWarning = false;

        internal void HandleStateDigest(StateDigestEvent evt)
        {
            if (!IsConnected || IsHost) return;

            var host = Participants.FirstOrDefault(p => p.UserId == evt.ExecutorId && p.Role == UserRole.Host);
            if (host == null) return;

            var digest = ComputeStateDigest();
            if (digest == null) return;

            var matched = digest == evt.Digest;
            var hostName = host.UserName;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (matched)
                {
                    HasDivergenceWarning = false;
                    return;
                }

                DivergenceWarningText = $"通信が途切れていた間に、{hostName} さん (ホスト) の内容とずれた可能性があります。";
                HasDivergenceWarning = true;
            });
        }

        private string? ComputeStateDigest()
        {
            if (Scenes == null) return null;

            try
            {
                var builder = new StringBuilder();
                foreach (var timeline in Scenes.Timelines)
                {
                    builder.Append(timeline.ID).Append('\n');

                    var lines = new List<string>();
                    foreach (var item in timeline.Items)
                    {
                        var json = JsonConvert.SerializeObject(item, ItemSerializerOptions.NullPath);
                        var itemHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                        lines.Add($"{ItemIdManager.GetOrCreateId(item)}|{item.Frame}|{item.Layer}|{item.Length}|{itemHash}");
                    }

                    lines.Sort(StringComparer.Ordinal);
                    foreach (var line in lines) builder.Append(line).Append('\n');
                }

                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] ComputeStateDigest failed: {ex.Message}");
                return null;
            }
        }

        private bool updateNoticeShown;

        private void HandleUpdateAvailable(string? latestVersion)
        {
            if (updateNoticeShown) return;
            updateNoticeShown = true;

            Application.Current?.Dispatcher.InvokeAsync(() =>
                PromptUpdate($"新しいバージョン{FormatVersion(latestVersion)}が公開されています。\n"
                    + "共同編集は最新のプラグイン同士でのみ利用できるため、更新するまで次回から接続できません。"));
        }

        private static string FormatVersion(string? version) =>
            string.IsNullOrWhiteSpace(version) ? " " : $" ({version}) ";

        private void HandleRoomNotFound(string? reason, string? latestVersion)
        {
            guestRoomValidation?.TrySetResult(false);
            ResetNetworkState();

            if (reason == "outdated")
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    PromptUpdate($"お使いのプラグイン ({UpdateChecker.Instance.CurrentVersion}) は古いため、共同編集サーバーに接続できません。\n"
                        + $"公開中の最新バージョンは{FormatVersion(latestVersion).TrimEnd()}です。"));
                return;
            }

            var message = reason switch
            {
                "expired" => "このルームは一定時間やり取りが無かったため、自動的に閉じられました。\nホストに新しいルームを作り直してもらってください。",
                "host_key" => "このルームにはホストとして接続できません。\n新しいルームを作成してください。",
                _ => "ルームが存在しません。ルームIDを確認してください。"
            };

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, "接続エラー", MessageBoxButton.OK);
            });
        }

        private void HandleConnectionStateChanged(bool connected)
        {
            if (!connected)
            {
                ResetNetworkState();
                return;
            }

            if (localUserRole == UserRole.Host)
            {
                IsConnected = true;
                RefreshCommandStates();
            }
        }

        public async Task KickUserAsync(Guid targetUserId)
        {
            var evt = new UserKickedEvent(targetUserId)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            await sessionClient.SendAsync(null, evt);

            var participant = Participants.FirstOrDefault(p => p.UserId == targetUserId);
            if (participant != null)
            {
                CommitParticipationTime(participant);
                Participants.Remove(participant);
            }
        }

        internal void HandleUserKickedEvent(UserKickedEvent evt)
        {
            if (evt.TargetUserId == LocalUserId)
            {
                Application.Current?.Dispatcher.InvokeAsync(async () =>
                {
                    MessageBox.Show("ホストによってルームからキックされたため、接続が切断されました。", "キック通知", MessageBoxButton.OK);
                    await StopNetworkAsync();
                });
            }
            else
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    var p = Participants.FirstOrDefault(x => x.UserId == evt.TargetUserId);
                    if (p != null)
                    {
                        CommitParticipationTime(p);
                        Participants.Remove(p);
                    }
                });
            }
        }

        private void HandlePeerDisconnected(Guid userId, bool isHost)
        {
            if (isHost)
            {
                Application.Current?.Dispatcher.InvokeAsync(() => BeginHostWatch(userId));
                return;
            }

            HandleUserLeftEvent(new UserLeftEvent(userId, isHost)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = userId
            });
        }

        private static readonly TimeSpan HostProbeInterval = TimeSpan.FromSeconds(15);
        private const int HostProbeCount = 4;

        private Guid watchedHostId;

        private void BeginHostWatch(Guid hostUserId)
        {
            if (hostUserId == LocalUserId || watchedHostId == hostUserId) return;

            watchedHostId = hostUserId;

            var host = Participants.FirstOrDefault(p => p.UserId == hostUserId);
            host?.Status = UserStatus.Disconnected;

            _ = WatchHostAsync(hostUserId);
        }

        private void NotePeerAlive(Guid userId)
        {
            if (watchedHostId == userId) watchedHostId = Guid.Empty;
        }

        private async Task WatchHostAsync(Guid hostUserId)
        {
            while (true)
            {
                for (var i = 0; i < HostProbeCount; i++)
                {
                    await Task.Delay(HostProbeInterval);

                    if (disposed || !IsConnected || watchedHostId != hostUserId) return;

                    var probe = new PresenceEvent(LocalUserId, UserName, localUserRole, false, LocalProfileId, UserDescription, UpdateChecker.Instance.CurrentVersion)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    };
                    await sessionClient.SendAsync(null, probe);
                }

                if (disposed || !IsConnected || watchedHostId != hostUserId) return;

                var hostName = Participants.FirstOrDefault(p => p.UserId == hostUserId)?.UserName ?? "ホスト";
                var keepWaiting = MessageBox.Show(
                    $"{hostName} との接続が切れたまま約1分が経過し、応答がありません。\n" +
                    "YMM4が落ちたか、通信が途切れている可能性があります。\n\n" +
                    "ルーム自体はまだ開いているため、復帰を待つこともできます。\n" +
                    "待機を続けますか？（いいえ を選ぶと切断します）",
                    "ホストの応答がありません",
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes;

                if (disposed || !IsConnected || watchedHostId != hostUserId) return;

                if (!keepWaiting)
                {
                    watchedHostId = Guid.Empty;
                    await StopNetworkAsync();
                    return;
                }
            }
        }

        internal void HandleUserLeftEvent(UserLeftEvent evt)
        {
            if (evt.UserId == LocalUserId) return;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (evt.IsHost)
                {
                    MessageBox.Show("ホストがルームを終了したため、接続が切断されました。", "ルーム終了", MessageBoxButton.OK);
                    _ = StopNetworkAsync();
                }
                else
                {
                    var participant = Participants.FirstOrDefault(p => p.UserId == evt.UserId);
                    if (participant != null)
                    {
                        CommitParticipationTime(participant);
                        Participants.Remove(participant);
                    }
                }
            });
        }

        internal void HandleItemLockedEvent(ItemLockedEvent evt)
        {
            if (locallyLockedItems.TryGetValue(evt.ItemId, out var myTimestamp))
            {
                bool theyWin = evt.LockTimestamp < myTimestamp ||
                               (evt.LockTimestamp == myTimestamp &&
                                string.Compare(evt.UserId.ToString(), LocalUserId.ToString(), StringComparison.Ordinal) < 0);

                if (theyWin)
                {
                    locallyLockedItems.Remove(evt.ItemId);
                    LockedItems[evt.ItemId] = evt.UserId;
                    lockedItemsReceivedAt[evt.ItemId] = DateTime.UtcNow;
                    ForceDeselectItem(evt.ItemId);
                    RequestItemState(evt.ItemId, evt.UserId);
                }
                return;
            }

            LockedItems[evt.ItemId] = evt.UserId;
            lockedItemsReceivedAt[evt.ItemId] = DateTime.UtcNow;
        }

        internal void HandleItemUnlockedEvent(ItemUnlockedEvent evt)
        {
            if (!LockedItems.TryGetValue(evt.ItemId, out var owner) || owner != evt.UserId) return;

            LockedItems.Remove(evt.ItemId);
            lockedItemsReceivedAt.Remove(evt.ItemId);
        }

        internal void HandleCursorMovedEvent(CursorMovedEvent evt)
        {
            if (evt.ExecutorId == Guid.Empty || evt.ExecutorId == LocalUserId) return;
            var p = EnsureParticipant(evt.ExecutorId);
            p.CurrentFrame = evt.CurrentFrame;
            p.CurrentTimelineIndex = evt.TimelineIndex;
            p.IsPlaying = evt.IsPlaying;
            p.FrameUpdatedAt = DateTime.UtcNow;

            if (!IsHost && CurrentUserPermission.CanSyncSeekPosition && p.Role == UserRole.Host)
            {
                _ = SyncPlaybackStateAsync(evt.CurrentFrame, evt.IsPlaying);
            }
        }

        private int lastAppliedRemoteFrame = -1;
        private DateTime lastAppliedRemoteFrameAt = DateTime.MinValue;

        private async Task SyncPlaybackStateAsync(int frame, bool isPlaying)
        {
            lastAppliedRemoteFrame = frame;
            lastAppliedRemoteFrameAt = DateTime.UtcNow;

            if (!isPlaying && FirstOrDefaultTimeline != null)
            {
                var previous = isApplyingRemoteEvent;
                isApplyingRemoteEvent = true;
                try
                {
                    FirstOrDefaultTimeline.UnlockCurrentFrame();
                    FirstOrDefaultTimeline.CurrentFrame = frame;
                }
                finally
                {
                    isApplyingRemoteEvent = previous;
                }
            }

            if (previewSeekAction != null)
            {
                await previewSeekAction(frame, isPlaying);
            }
        }

        internal void HandleCharacterRequest(CharacterRequestEvent evt) => characterShareManager.HandleRequest(evt);

        internal void HandleCharacterShared(CharacterSharedEvent evt) => characterShareManager.HandleShared(evt);

        internal static bool IsCharacterDecided(string characterName) => CharacterShareManager.IsDecided(characterName);

        internal void RequestCharacter(string characterName, Guid ownerId, Action resume)
        {
            characterShareManager.WhenDecided(characterName, resume);
            characterShareManager.RequestIfNeeded(characterName, ownerId);
        }

        internal void HandleFileAvailable(FileAvailableEvent evt)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var needsTransfer = await fileTransferManager.NeedsTransferAsync(evt);

                    var requestEvt = new FileRequestEvent(evt.TransferId, LocalUserId, needsTransfer)
                    {
                        DateTime = DateTime.UtcNow,
                        ExecutorId = LocalUserId
                    };

                    await sessionClient.SendAsync(evt.ExecutorId.ToString(), requestEvt);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MultiUserEdit] HandleFileAvailable failed: {ex.Message}");
                }
            });
        }

        internal void HandleFileRequest(FileRequestEvent evt)
        {
            _ = fileTransferManager.HandleFileRequestAsync(evt, sessionClient, LocalUserId);
        }

        internal void HandleFileTransferStart(FileTransferStartEvent evt)
        {
            fileTransferManager.HandleTransferStart(evt);
        }

        internal void HandleFileChunk(FileChunkEvent evt)
        {
            fileTransferManager.HandleChunk(evt);
        }

        public Task SendFileAsync(string filePath) =>
            fileTransferManager.SendFileAsync(filePath, sessionClient, LocalUserId);

        private void ForceDeselectItem(Guid itemId)
        {
            if (Scenes == null) return;

            var previous = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            try
            {
                foreach (var timeline in Scenes.Timelines)
                {
                    var item = timeline.SelectedItems.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == itemId);
                    if (item == null) continue;

                    try
                    {
                        var selectedItems = timeline.SelectedItems;
                        var toRemove = selectedItems.Where(i => ItemIdManager.GetOrCreateId(i) == itemId).ToList();
                        foreach (var rm in toRemove)
                            selectedItems.Remove(rm);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MultiUserEdit] ForceDeselect failed: {ex.Message}");
                    }
                    break;
                }
            }
            finally
            {
                isApplyingRemoteEvent = previous;
            }
        }

        private void FirstOrDefaultTimeline_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Timeline.CurrentFrame) && IsConnected && !isApplyingRemoteEvent)
                SendCursorMovedAsync(FirstOrDefaultTimeline!.CurrentFrame);
        }

        public async void SendCursorMovedAsync(int currentFrame)
        {
            try
            {
                var isPlaying = getIsPlayingFunc?.Invoke() ?? false;
                if (!isPlaying
                    && currentFrame == lastAppliedRemoteFrame
                    && DateTime.UtcNow - lastAppliedRemoteFrameAt < CursorEchoWindow) return;

                var timelineIndex = Scenes?.Timelines.IndexOf(FirstOrDefaultTimeline!) ?? 0;
                await eventSender.SendCursorMovedThrottledAsync(currentFrame, timelineIndex, isPlaying);
            }
            catch { }
        }

        private void OnUndoRedoCommandCreated(object? sender, EventArgs e)
        {
            if (isApplyingRemoteEvent || !IsConnected) return;

            if (FirstOrDefaultTimeline != null && Scenes != null)
            {
                var index = Scenes.Timelines.IndexOf(FirstOrDefaultTimeline);
                if (index < 0) index = 0;

                foreach (var item in FirstOrDefaultTimeline.SelectedItems)
                {
                    _ = eventSender.SendItemUpdatedAsync(item, index);
                }
            }
        }

        public void LockItemLocally(Guid itemId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            locallyLockedItems[itemId] = timestamp;
            _ = eventSender.SendItemLockAsync(itemId, timestamp);
        }

        public void UnlockItemLocally(Guid itemId)
        {
            if (!locallyLockedItems.Remove(itemId)) return;
            _ = eventSender.SendItemUnlockAsync(itemId);
        }

        internal bool IsReceivingFiles() => fileTransferManager.IsReceivingAny();

        internal bool IsItemEditableLocally(Guid itemId) =>
            locallyLockedItems.ContainsKey(itemId) || !LockedItems.ContainsKey(itemId);

        internal void HandleItemStateRequest(ItemStateRequestEvent evt)
        {
            if (!IsConnected || Scenes == null) return;

            foreach (var timeline in Scenes.Timelines)
            {
                var item = timeline.Items.FirstOrDefault(i => ItemIdManager.GetOrCreateId(i) == evt.ItemId);
                if (item == null) continue;

                eventSender.ClearBaseline(evt.ItemId);
                _ = eventSender.SendItemUpdatedAsync(item, Scenes.Timelines.IndexOf(timeline));
                return;
            }
        }

        private void RequestItemState(Guid itemId, Guid ownerId)
        {
            if (!IsConnected) return;

            var request = new ItemStateRequestEvent(itemId, LocalUserId)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            _ = sessionClient.SendAsync(ownerId.ToString(), request);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            ResourceAvailabilityChecker.MissingReported = null;

            AppDomain.CurrentDomain.ProcessExit -= CurrentDomain_ProcessExit;
            Application.Current?.Exit -= Application_Exit;

            presenceTimer.Stop();
            presenceTimer.Tick -= PresenceTimer_Tick;

            FirstOrDefaultTimeline = null;

            sessionClient.EventReceived -= HandleEventReceived;
            sessionClient.Disconnected -= HandleDisconnected;
            sessionClient.RoomNotFound -= HandleRoomNotFound;
            sessionClient.UpdateAvailable -= HandleUpdateAvailable;
            sessionClient.PeerDisconnected -= HandlePeerDisconnected;
            sessionClient.ConnectionStateChanged -= HandleConnectionStateChanged;

            undoRedoManager?.UndoRedoCommandCreated -= OnUndoRedoCommandCreated;

            if (Scenes is not null)
            {
                UnsubscribeScenesEvents();
                foreach (var timeline in Scenes.Timelines) timelineSyncManager.UnregisterTimeline(timeline);
            }

            locallyLockedItems.Clear();
            lockedItemsReceivedAt.Clear();

            fileTransferManager.CancelAll();
            adornerManager.DetachAdorner();

            _ = sessionClient.StopAsync();

            GC.SuppressFinalize(this);
        }
    }
}
