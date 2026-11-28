using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using MultiUserEdit.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.UndoRedo;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession : Bindable, IDisposable
    {
        private readonly SessionClient sessionClient;
        internal SessionClient SessionClient => sessionClient;

        private readonly ClientEventDispatcher eventDispatcher;

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
        private readonly DisposeCollector disposer = new();
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
        private readonly ConcurrentDictionary<Guid, bool> peerFileChannels = new();

        private readonly ConcurrentDictionary<Guid, UserPermission> permissionOverrides = new();

        private readonly ConcurrentDictionary<Guid, byte> webParticipants = new();
        private readonly ConcurrentDictionary<Guid, byte> directPeers = new();
        private readonly PeerTransport? peerTransport;

        private readonly PreviewService previewService;
        private readonly PreviewStreams previewStreams;
        private readonly Timer renderStatusTimer;
        private string lastRenderStatus = string.Empty;
        private long lastRenderStatusAt;
        private static readonly TimeSpan RenderStatusInterval = TimeSpan.FromSeconds(3);
        private const long RenderStatusHeartbeatMs = 20000;

        private readonly WebAssistService webAssist;

        private static readonly UserPermission FullPermission = UserPermission.CreateFromLevel(PermissionLevel.Full);

        private readonly HashSet<Guid> rejectedLocalChanges = [];

        private readonly Lock hostIdLock = new();
        private Guid verifiedHostIdValue;

        private Guid VerifiedHostId
        {
            get { lock (hostIdLock) return verifiedHostIdValue; }
            set { lock (hostIdLock) verifiedHostIdValue = value; }
        }

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

        public ObservableCollection<ScriptEntry> Scripts { get; } = [];

        public ICommand AddScriptsToTimelineCommand { get; private set; } = null!;

        public ICommand DeleteScriptCommand { get; private set; } = null!;

        private const int MaxScripts = 500;
        private const int MaxSerifLength = 2000;
        private const int MaxNameLength = 100;

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
        public ICommand DeleteKeptRoomCommand { get; }
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
                Texts.SyncData,
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
            return string.IsNullOrWhiteSpace(name) ? Texts.Collaborator : name;
        }

        private void SyncNow()
        {
            if (!IsConnected) return;

            if (IsKeptRoom)
            {
                RequestServerSync();
                return;
            }

            if (!IsHost)
            {
                var host = Participants.FirstOrDefault(p => p.Role == UserRole.Host && p.UserId != LocalUserId);
                if (host != null) SyncFrom(host.UserId);
                return;
            }

            {
                var confirmed = MessageBox.Show(
                    Texts.SyncAllConfirm,
                    Texts.SyncData,
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
        public string RoomId
        {
            get => roomId;
            set
            {
                if (Set(ref roomId, value)) RefreshCommandStates();
            }
        }

        private string inputRoomId = string.Empty;
        public string InputRoomId
        {
            get => inputRoomId;
            set
            {
                Set(ref inputRoomId, RoomIdParser.Parse(value) ?? value?.Trim() ?? string.Empty);
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

        private bool CanDirectNow => peerTransport != null && DirectTransferChoice.Current != false;

        private void OnDirectTransferDecided(bool useDirect)
        {
            var transport = peerTransport;
            if (transport == null) return;
            _ = Task.Run(() => transport.ResolveDeferred(useDirect));
            if (!useDirect) BroadcastLocalPresence();
        }

        private void BroadcastLocalPresence()
        {
            if (!IsConnected) return;

            var presenceEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, true, LocalProfileId, UserDescription, UpdateChecker.Instance.CurrentVersion, sessionClient.HasFileChannel)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId,
                CanDirect = CanDirectNow,
                CanCatchUp = true
            };
            _ = sessionClient.SendAsync(null, presenceEvt);
        }

        public bool IsSpectator =>
            !IsHost
            && CurrentUserPermission.Level is PermissionLevel.ReadOnly or PermissionLevel.LiveMirror
            && !CurrentUserPermission.CanAddItems
            && !CurrentUserPermission.CanMoveItems
            && !CurrentUserPermission.CanDeleteItems
            && !CurrentUserPermission.CanEditProperties;

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

        private string transferSpeedText = string.Empty;
        public string TransferSpeedText
        {
            get => transferSpeedText;
            set => Set(ref transferSpeedText, value);
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
            private set
            {
                if (Set(ref isConnected, value)) RefreshCommandStates();
            }
        }

        public CollaborationSession(Scenes scenes)
        {
            Scenes = scenes;
            sessionClient = new SessionClient(new WebsocketProvider(coalesce: true), userId => new WebsocketProvider(userId));
            eventDispatcher = new ClientEventDispatcher();
            adornerManager = new AdornerManager(() => MainView);
            fileTransferManager = new FileTransferManager
            {
                GetPeerCount = () => Participants.Count(p => p.UserId != LocalUserId && !p.IsWeb),
                GetUserName = GetParticipantName,
                FileSendBlockedReason = DescribeFileSendBlock
            };
            eventSender = new EditEventSender(sessionClient, fileTransferManager, () => LocalUserId)
            {
                IsItemAlive = item => Scenes?.Timelines.Any(timeline => timeline.Items.Contains(item)) ?? false,
                IsSendingSuppressed = () => IsSpectator
            };
            timelineSyncManager = new TimelineSyncManager(eventSender, () => isApplyingRemoteEvent, IsItemEditableLocally)
            {
                RejectAddition = RejectRestrictedAddition,
                RejectRemoval = RejectRestrictedRemoval,
                NoteLocalAddition = NoteLocallyAddedItem,
                IsLocalChangeAllowed = IsLocalChangeAllowed,
                RejectLocalChange = RejectLocalChange
            };
            characterShareManager = new CharacterShareManager(
                sessionClient,
                fileTransferManager,
                () => LocalUserId,
                userId => Participants.FirstOrDefault(p => p.UserId == userId)?.UserName ?? string.Empty,
                () => activeViewModel,
                ExecuteRemoteAction,
                () => MainView);

            fileTransferManager.GetPeerIds = () => [.. peerFileChannels.Keys.Where(id => !webParticipants.ContainsKey(id))];
            fileTransferManager.PeerHasFileChannel = userId =>
                peerFileChannels.TryGetValue(userId, out var hasChannel) && hasChannel;
            fileTransferManager.IsPeerPresent = userId => peerFileChannels.ContainsKey(userId);
            if (Settings.MultiUserEditSettings.Default.UseDirectTransfer && PeerTransport.IsAvailable)
            {
                peerTransport = new PeerTransport((target, signal) => sessionClient.SendAsync(target.ToString(), signal with { ExecutorId = LocalUserId }))
                {
                    AcceptTransfer = fileTransferManager.AcceptDirectTransfer,
                    ReceiveChunk = fileTransferManager.HandleChunkBytes,
                    ReceiveAck = (from, transferId, index) => fileTransferManager.HandleChunkAck(new FileChunkAckEvent(transferId, index) { ExecutorId = from }),
                    IsCancelled = fileTransferManager.IsCancelledBy,
                    AllowIncoming = remote =>
                    {
                        var choice = DirectTransferChoice.Current;
                        if (choice == null) _ = DirectTransferChoice.AskAsync();
                        return choice;
                    },
                };
                fileTransferManager.Peers = peerTransport;
                fileTransferManager.CanUseDirect = userId => directPeers.ContainsKey(userId);
                fileTransferManager.ConfirmDirect = DirectTransferChoice.AskAsync;
                DirectTransferChoice.Decided += OnDirectTransferDecided;
            }
            fileTransferManager.SendChunkAck = (senderId, transferId, chunkIndex) =>
            {
                if (senderId == Guid.Empty) return;

                var ackEvt = new FileChunkAckEvent(transferId, chunkIndex)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                var viaFileChannel = sessionClient.HasFileChannel
                    && peerFileChannels.TryGetValue(senderId, out var senderHasChannel) && senderHasChannel;
                _ = viaFileChannel
                    ? sessionClient.SendFileDataToManyAsync([senderId.ToString()], ackEvt)
                    : sessionClient.SendToManyAsync([senderId.ToString()], ackEvt);
            };

            fileTransferManager.DeclineTransfer = (senderId, transferId) =>
            {
                if (senderId == Guid.Empty || senderId == LocalUserId) return;

                var cancelEvt = new FileTransferCancelEvent(transferId, LocalUserId)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                _ = sessionClient.SendToManyAsync([senderId.ToString()], cancelEvt);
            };

            sessionClient.FileChannelChanged += () => Application.Current?.Dispatcher.InvokeAsync(BroadcastLocalPresence);
            sessionClient.HostIdentified += hostId => VerifiedHostId = hostId;
            sessionClient.KeptRoomJoined += hasSnapshot =>
            {
                isKeptRoom = true;
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    RefreshCommandStates();
                    HandleKeptRoomJoined(hasSnapshot);
                });
            };
            sessionClient.SnapshotRequested += () => Application.Current?.Dispatcher.InvokeAsync(RequestSnapshotUpload);
            sessionClient.ResyncRequired += () => Application.Current?.Dispatcher.InvokeAsync(RequestServerSync);
            fileTransferManager.ReceiveBlockedReason = DescribeReceiveBlock;
            fileTransferManager.KeepsReceivedFiles = () => isKeptRoom;

            fileTransferManager.TransferCompleted += (transferId, path) => FileTransferCompleted?.Invoke(transferId, path);
            fileTransferManager.SentFilesChanged += () => SentFilesChanged?.Invoke();
            fileTransferManager.TransferSummaryChanged += OnTransferSummaryChanged;

            LocalUserId = sessionClient.LocalUserId;

            previewService = new PreviewService(
                () => Scenes,
                userId => Participants.Any(p => p.UserId == userId),
                SendPreviewFrameAsync);
            previewStreams = new PreviewStreams(previewService, PeerTransport.IceServers,
                (target, signal) => sessionClient.SendAsync(target.ToString(), signal with { ExecutorId = LocalUserId }));
            RenderReadiness.Clear();
            renderStatusTimer = new Timer(_ => PublishRenderStatus(), null, RenderStatusInterval, RenderStatusInterval);

            webAssist = new WebAssistService(
                () => Scenes,
                userId => Participants.Any(p => p.UserId == userId) && PermissionOf(userId).CanAddItems,
                (target, reply) => sessionClient.SendAsync(target.ToString(), reply with { ExecutorId = LocalUserId }),
                ResolveMediaForWeb,
                ConfirmMediaForWebAsync,
                SendMediaToWeb,
                name => fileTransferManager.IsReceivingFile(name));

            missingReported = SendMissingResource;
            ResourceAvailabilityChecker.MissingReported = missingReported;

            sessionClient.EventReceived += HandleEventReceived;
            sessionClient.Disconnected += HandleDisconnected;
            sessionClient.RoomNotFound += HandleRoomNotFound;
            sessionClient.UpdateAvailable += HandleUpdateAvailable;
            sessionClient.PeerDisconnected += HandlePeerDisconnected;
            sessionClient.ConnectionStateChanged += HandleConnectionStateChanged;

            CreateRoomCommand = new ActionCommand(_ => !IsConnected && !isJoining, async _ => await CreateRoomAsync(selectedRoomKind));
            JoinRoomCommand = new ActionCommand(_ => !IsConnected && !isJoining && RoomIdParser.Parse(InputRoomId) != null, async _ => await JoinRoomAsync());
            CopyRoomIdCommand = new ActionCommand(_ => IsConnected && !string.IsNullOrEmpty(RoomId), _ => Clipboard.SetText(RoomId));
            AddScriptsToTimelineCommand = new ActionCommand(_ => !addingScripts, async _ => await AddScriptsToTimelineAsync());
            DeleteScriptCommand = new ActionCommand(p => p is ScriptEntry, p => { if (p is ScriptEntry entry) DeleteScript(entry); });
            Scripts.CollectionChanged += OnScriptsChanged;
            EnsureTrailingScriptRow();
            CopyInviteLinkCommand = new ActionCommand(_ => IsConnected && !string.IsNullOrEmpty(RoomId), _ => Clipboard.SetText($"https://multi-user-edit.dolphin-discord-js.workers.dev/?roomId={RoomId}"));
            JoinFromClipboardCommand = new ActionCommand(_ => !IsConnected && !isJoining, async _ =>
            {
                if (Clipboard.ContainsText())
                {
                    if (RoomIdParser.Parse(Clipboard.GetText()) is { } extracted)
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
                    MessageBox.Show(Texts.DeepLinkRegistered, Texts.RegisterDone, MessageBoxButton.OK);
                }
            });
            UnregisterProtocolCommand = new ActionCommand(_ => true, _ =>
            {
                if (ProtocolRegister.UnregisterCustomProtocol())
                {
                    IsProtocolRegistered = false;
                    MessageBox.Show(Texts.DeepLinkUnregistered, Texts.UnregisterDone, MessageBoxButton.OK);
                }
            });
            DisconnectCommand = new ActionCommand(_ => IsConnected || isJoining, async _ => await StopNetworkAsync());
            DeleteKeptRoomCommand = new ActionCommand(_ => IsKeptRoomOwner, async _ => await DeleteKeptRoomAsync());
            SyncNowCommand = new ActionCommand(_ => IsConnected && !isJoining, _ => SyncNow());
            KickUserCommand = new ActionCommand(
                param => IsHost && param is Guid targetId && targetId != LocalUserId,
                async param =>
                {
                    if (param is Guid targetUserId)
                    {
                        var target = Participants.FirstOrDefault(p => p.UserId == targetUserId);
                        var name = target?.UserName ?? Texts.ThisUser;
                        var result = MessageBox.Show(string.Format(Texts.KickConfirm, name), Texts.KickConfirmTitle, MessageBoxButton.OKCancel);
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
            DeepLinkListener.Start();
            DeepLinkListener.TakePending(JoinFromDeepLink);
            Application.Current?.Dispatcher.InvokeAsync(WatchMainView, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            presenceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            presenceTimer.Tick += PresenceTimer_Tick;
            presenceTimer.Start();

            disposer.CollectAction(sessionClient, () => _ = sessionClient.StopAsync());
            disposer.CollectAction(adornerManager, adornerManager.DetachAdorner);
            disposer.CollectAction(fileTransferManager, fileTransferManager.CancelAll);
            disposer.CollectAction(locallyLockedItems, () =>
            {
                locallyLockedItems.Clear();
                lockedItemsReceivedAt.Clear();
            });
            disposer.CollectAction(timelineSyncManager, () =>
            {
                if (Scenes is null) return;
                UnsubscribeScenesEvents();
                foreach (var timeline in Scenes.Timelines) timelineSyncManager.UnregisterTimeline(timeline);
            });
            disposer.CollectAction(nameof(undoRedoManager), () =>
            {
                undoRedoManager?.UndoRedoCommandCreated -= OnUndoRedoCommandCreated;
                undoRedoManager?.Recorded -= UndoRedoManager_Recorded;
            });
            disposer.CollectAction(nameof(HandleEventReceived), () =>
            {
                sessionClient.EventReceived -= HandleEventReceived;
                sessionClient.Disconnected -= HandleDisconnected;
                sessionClient.RoomNotFound -= HandleRoomNotFound;
                sessionClient.UpdateAvailable -= HandleUpdateAvailable;
                sessionClient.PeerDisconnected -= HandlePeerDisconnected;
                sessionClient.ConnectionStateChanged -= HandleConnectionStateChanged;
            });
            disposer.CollectAction(nameof(FirstOrDefaultTimeline), () => FirstOrDefaultTimeline = null);
            disposer.CollectAction(presenceTimer, () =>
            {
                presenceTimer.Stop();
                presenceTimer.Tick -= PresenceTimer_Tick;
            });
            disposer.CollectAction(nameof(OnApplicationTerminating), () =>
            {
                AppDomain.CurrentDomain.ProcessExit -= CurrentDomain_ProcessExit;
                Application.Current?.Exit -= Application_Exit;
                watchedMainView?.Closed -= OnMainViewClosed;
            });
            disposer.Collect(peerTransport);
            disposer.CollectAction(nameof(OnDirectTransferDecided), () => DirectTransferChoice.Decided -= OnDirectTransferDecided);
            disposer.Collect(previewService);
            disposer.Collect(previewStreams);
            disposer.Collect(renderStatusTimer);
            disposer.CollectAction(nameof(missingReported), () =>
            {
                if (ResourceAvailabilityChecker.MissingReported == missingReported) ResourceAvailabilityChecker.MissingReported = null;
            });
        }

        private readonly Action<Guid, string[], string[]> missingReported;

        private void CurrentDomain_ProcessExit(object? sender, EventArgs e)
        {
            OnApplicationTerminating();
        }

        private void Application_Exit(object? sender, ExitEventArgs e)
        {
            OnApplicationTerminating();
        }

        private static readonly TimeSpan TerminationSendTimeout = TimeSpan.FromSeconds(2);

        private async Task SendCloseRoomAsync()
        {
            var notice = new { type = "close_room" };

            await sessionClient.SendAsync(null, notice);
            if (sessionClient.HasFileChannel) await sessionClient.SendFileDataAsync(null, notice);
        }

        private int terminationNoticeSent;

        private void OnApplicationTerminating()
        {
            if (Interlocked.Exchange(ref terminationNoticeSent, 1) != 0) return;

            AudioProxy.DeleteCache();

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
                    if (IsHost && !IsKeptRoom) SendCloseRoomAsync().Wait(TerminationSendTimeout);
                    _ = sessionClient.StopAsync();
                }
                catch { }
            }
        }

        private void JoinFromDeepLink(string roomId)
        {
            InputRoomId = roomId;
            if (IsConnected) return;
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(500);
                if (!IsConnected && !isJoining && !string.IsNullOrEmpty(InputRoomId)) await JoinRoomAsync();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
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
                    TransferSpeedText = string.Empty;
                    return;
                }

                Interlocked.Increment(ref transferDisplayGeneration);
                IsTransferring = true;
                TransferStatusText = summary.DisplayText;
                TransferProgress = summary.OverallProgress;
                TransferSpeedText = summary.SpeedText;
            });
        }

        private static readonly TimeSpan AwayThreshold = TimeSpan.FromMinutes(3);

        private void PresenceTimer_Tick(object? sender, EventArgs e)
        {
            WatchMainView();
            ExpireStalePlayback();

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

            RemoveLongDeparted();
            TryUploadSnapshot();

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

        private WeakReference<Window>? mainViewReference;

        public Window? MainView
        {
            get
            {
                if (mainViewReference?.TryGetTarget(out var known) == true && known.IsLoaded) return known;
                if (MainViewLocator.Find(Scenes) is not { } found) return null;
                mainViewReference = new WeakReference<Window>(found);
                return found;
            }
        }

        private Window? watchedMainView;

        private void WatchMainView()
        {
            if (watchedMainView != null || disposed || MainView is not { } window) return;
            watchedMainView = window;
            window.Closed += OnMainViewClosed;
        }

        private void OnMainViewClosed(object? sender, EventArgs e)
        {
            if (sender is Window window) window.Closed -= OnMainViewClosed;
            OnApplicationTerminating();
            Dispose();
        }

        public void SetTimelineToolInfo(TimelineToolInfo info, MultiUserEditViewModel viewModel)
        {
            addItemStatus = info.AsyncAwaitStatus;
            undoRedoManager?.UndoRedoCommandCreated -= OnUndoRedoCommandCreated;
            undoRedoManager?.Recorded -= UndoRedoManager_Recorded;
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
            undoRedoManager?.Recorded += UndoRedoManager_Recorded;

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

            if (!CurrentUserPermission.CanAddScenes && currentTimelines.Count > scenesSnapshot.Count)
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
                        SendSceneAdded(i, t.Name, t.ID);
                    }
                }
            }

            for (int i = 0; i < scenesSnapshot.Count; i++)
            {
                var oldT = scenesSnapshot[i];
                if (!currentTimelines.Contains(oldT))
                {
                    timelineSyncManager.UnregisterTimeline(oldT);
                    SendSceneRemoved(i);
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

        private void SendSceneAdded(int index, string name, Guid sceneId)
        {
            if (CurrentUserPermission.CanAddScenes) _ = eventSender.SendSceneAddedAsync(index, name, sceneId);
        }

        private void SendSceneRemoved(int index)
        {
            if (CurrentUserPermission.CanDeleteScenes) _ = eventSender.SendSceneRemovedAsync(index);
        }

        public void UpdateGlobalPermission(UserPermission newPerm)
        {
            permissionOverrides.Clear();
            GlobalDefaultPermission.CopyFrom(newPerm);

            foreach (var p in Participants)
            {
                p.Permission = newPerm;
            }

            _ = sessionClient.SendAsync(null, new PermissionUpdatedEvent(null, newPerm)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
        }

        public void UpdateUserPermission(Guid targetUserId, UserPermission newPerm)
        {
            var p = Participants.FirstOrDefault(x => x.UserId == targetUserId);
            p?.Permission = newPerm;
            permissionOverrides[targetUserId] = newPerm;

            _ = sessionClient.SendAsync(null, new PermissionUpdatedEvent(targetUserId, newPerm)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });
        }

        internal void HandlePermissionUpdated(PermissionUpdatedEvent evt)
        {
            if (!IsFromHost(evt.ExecutorId)) return;

            if (evt.TargetUserId is { } targetId && targetId != Guid.Empty)
                permissionOverrides[targetId] = evt.Permission;
            else
                permissionOverrides.Clear();

            if (evt.TargetUserId == null || evt.TargetUserId == Guid.Empty)
            {
                GlobalDefaultPermission.CopyFrom(evt.Permission);

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

            var showedSceneTabs = SceneTabRefresher.ShowsSceneTabs(Scenes);

            try
            {
                var sceneId = SceneIdMap.ToLocal(evt.SceneId);
                if (sceneId != Guid.Empty && Scenes.Timelines.Any(t => t.ID == sceneId)) return;

                var index = Math.Clamp(evt.Index, 0, Scenes.Timelines.Count);

                var timeline = CreateSceneForSync(new OnlineTimeline
                {
                    Id = sceneId,
                    Name = evt.Name
                }, index);

                if (timeline == null)
                {
                    ErrorNotifier.NotifyList(
                        Texts.SceneUnavailableTitle,
                        Texts.SceneUnavailableLead,
                        ErrorNotifier.Quote(evt.Name),
                        Texts.SceneUnavailableNote);
                    return;
                }

                if (!string.IsNullOrEmpty(evt.Name)) timeline.Name = evt.Name;

                TrackRemoteSceneChanges();
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not add a scene from another participant", ex);
            }

            if (showedSceneTabs != SceneTabRefresher.ShowsSceneTabs(Scenes)) RequestSceneTabRefresh();
        }

        internal void HandleSceneRemoved(SceneRemovedEvent evt)
        {
            if (Scenes == null) return;

            var showedSceneTabs = SceneTabRefresher.ShowsSceneTabs(Scenes);

            try
            {
                var timeline = Scenes.Timelines.ElementAtOrDefault(evt.Index);
                if (timeline != null)
                {
                    Scenes.DeleteScene(timeline);
                    TrackRemoteSceneChanges();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not remove a scene for another participant", ex);
            }

            if (showedSceneTabs != SceneTabRefresher.ShowsSceneTabs(Scenes)) RequestSceneTabRefresh();
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
            OnPropertyChanged(nameof(IsKeptRoomOwner));
            OnPropertyChanged(nameof(RoomId));
            OnPropertyChanged(nameof(InputRoomId));
            (CreateRoomCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (JoinRoomCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (CopyRoomIdCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (CopyInviteLinkCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (JoinFromClipboardCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (DeleteKeptRoomCommand as ActionCommand)?.RaiseCanExecuteChanged();
            (SyncNowCommand as ActionCommand)?.RaiseCanExecuteChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private static string SyncFromConfirmMessage(string sourceName) =>
            string.Format(Texts.SyncFromConfirm, sourceName);

        private readonly SyncScenesTransfer syncScenesTransfer = new();

        private bool sceneTabRefreshRequested;

        private void RequestSceneTabRefresh()
        {
            if (sceneTabRefreshRequested) return;

            sceneTabRefreshRequested = true;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                sceneTabRefreshRequested = false;
                SceneTabRefresher.Refresh(undoRedoManager);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private bool isApplyingSyncScenes;

        private int TimelineIndexOf(Timeline timeline) => Scenes?.Timelines.IndexOf(timeline) ?? -1;

        private void SelectRoomKind(RoomKind kind) =>
            Set(ref selectedRoomKind, kind, nameof(IsNormalRoomSelected), nameof(IsKeptRoomSelected));

        public bool IsNormalRoomSelected
        {
            get => selectedRoomKind == RoomKind.Normal;
            set
            {
                if (value) SelectRoomKind(RoomKind.Normal);
            }
        }

        public bool IsKeptRoomSelected
        {
            get => selectedRoomKind == RoomKind.Kept;
            set
            {
                if (value) SelectRoomKind(RoomKind.Kept);
            }
        }

        private volatile bool isKeptRoom;

        public bool IsKeptRoom => isKeptRoom;

        public bool IsKeptRoomOwner => IsConnected && IsHost && isKeptRoom;

        private async Task DeleteKeptRoomAsync()
        {
            if (!IsKeptRoomOwner) return;
            if (MessageBox.Show(Texts.DeleteKeptRoomConfirm, Texts.DeleteKeptRoom, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

            var roomId = RoomId;
            await SendCloseRoomAsync();
            Settings.MultiUserEditSettings.Default.SetKeptRoomKey(roomId, null);
            await StopNetworkAsync();
        }

        private async Task CreateRoomAsync(RoomKind kind)
        {
            if (!await EnsureLatestPluginAsync()) return;

            var (relayReachable, relayProblem) = await RelayAvailabilityChecker.CheckAsync();
            if (!relayReachable && kind == RoomKind.Kept)
            {
                MessageBox.Show(RelayConnectionFailedMessage(relayProblem), Texts.RelayUnavailableTitle, MessageBoxButton.OK);
                return;
            }

            RoomId = Guid.NewGuid().ToString();
            isKeptRoom = kind == RoomKind.Kept;
            sessionClient.UsesBackupServer = !relayReachable;
            sessionClient.HostRoomKind = isKeptRoom ? "keep" : "normal";
            hostKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            if (kind == RoomKind.Kept) Settings.MultiUserEditSettings.Default.SetKeptRoomKey(RoomId, hostKey);
            localUserRole = UserRole.Host;
            IsHost = true;
            CurrentUserPermission = UserPermission.CreateFromLevel(PermissionLevel.Full);
            await ConnectNetworkAsync(RoomId);
        }

        private async Task JoinRoomAsync() => await JoinRoomAsync(asOwnerIfPossible: true, useBackupServer: false);

        private async Task JoinRoomAsync(bool asOwnerIfPossible, bool useBackupServer)
        {
            if (RoomIdParser.Parse(InputRoomId) is not { } roomId) return;
            if (!await EnsureLatestPluginAsync()) return;

            RoomId = roomId;
            sessionClient.UsesBackupServer = useBackupServer;

            if (asOwnerIfPossible
                && !useBackupServer
                && Settings.MultiUserEditSettings.Default.GetKeptRoomKey(RoomId) is { } ownerKey)
            {
                hostKey = ownerKey;
                localUserRole = UserRole.Host;
                IsHost = true;
                isKeptRoom = true;
                sessionClient.HostRoomKind = "keep";
                CurrentUserPermission = UserPermission.CreateFromLevel(PermissionLevel.Full);
            }
            else
            {
                localUserRole = UserRole.Guest;
                IsHost = false;
                isKeptRoom = false;
                sessionClient.HostRoomKind = null;
            }

            await ConnectNetworkAsync(RoomId);
        }

        private string? hostKey;

        private static async Task<bool> EnsureLatestPluginAsync()
        {
            var checker = UpdateChecker.Instance;
            if (!await checker.IsOutdatedAsync(TimeSpan.FromSeconds(3))) return true;

            PromptUpdate(string.Format(Texts.NewVersionLatestOnly, checker.LatestVersion));
            return false;
        }

        private static void PromptUpdate(string reason)
        {
            var result = MessageBox.Show(
                string.Format(Texts.OpenUpdatePageQuestion, reason),
                Texts.UpdateRequiredTitle,
                MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes) UpdateChecker.Instance.OpenUpdatePage();
        }

        private static string RelayConnectionFailedMessage(string detail) =>
            string.Format(Texts.RelayConnectFailed, detail);

        private TaskCompletionSource<bool>? guestRoomValidation;
        private string? relayFailureDetail;
        private bool isJoining;

        private int connectGeneration;

        private async Task ConnectNetworkAsync(string targetRoomId)
        {
            var myGeneration = Interlocked.Increment(ref connectGeneration);
            bool IsCurrent() => Volatile.Read(ref connectGeneration) == myGeneration;

            isJoining = true;
            WatchMainView();
            RefreshCommandStates();
            hostLeftHandled = false;
            keptRoomJoined = false;
            snapshotUploadPending = false;
            lastAppliedSeq = 0;
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
                ClearSharedScripts();
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

                var presenceEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, false, LocalProfileId, UserDescription, UpdateChecker.Instance.CurrentVersion, sessionClient.HasFileChannel)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId,
                    CanDirect = CanDirectNow,
                    CanCatchUp = true
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

                if (IsCurrent() && localUserRole == UserRole.Guest && !sessionClient.UsesBackupServer)
                {
                    relayFailureDetail = RelayAvailabilityChecker.Describe(ex);
                    Application.Current?.Dispatcher.InvokeAsync(async () =>
                    {
                        InputRoomId = targetRoomId;
                        await JoinRoomAsync(asOwnerIfPossible: false, useBackupServer: true);
                    });
                    return;
                }

                if (IsCurrent() && (!sessionClient.UsesBackupServer || relayFailureDetail != null))
                {
                    var detail = relayFailureDetail ?? RelayAvailabilityChecker.Describe(ex);
                    relayFailureDetail = null;

                    Application.Current?.Dispatcher.InvokeAsync(() => MessageBox.Show(
                        RelayConnectionFailedMessage(detail),
                        Texts.ConnectionError,
                        MessageBoxButton.OK));
                }
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

                    if (IsHost && !IsKeptRoom) await SendCloseRoomAsync();
                }
                await sessionClient.StopAsync();
            }
            catch
            {
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
                isKeptRoom = false;
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
                ClearSharedScripts();
                peerFileChannels.Clear();
                webParticipants.Clear();
                catchUpPeers.Clear();
                syncScenesTransfer.Clear();
                permissionOverrides.Clear();
                VerifiedHostId = Guid.Empty;
                MoveOrdering.Clear();
                departedAt.Clear();
                lock (wantedFiles)
                {
                    wantedFiles.Clear();
                    pendingWants.Clear();
                    pulledFiles.Clear();
                }
                lock (rejectedLocalChanges) rejectedLocalChanges.Clear();
                locallyLockedItems.Clear();
                lock (locallyAddedItems) locallyAddedItems.Clear();
                lockedItemsReceivedAt.Clear();
                LockedItems.Clear();
                OperatingItems.Clear();
                fileTransferManager.CancelAll();
                characterShareManager.Reset();
                SceneIdMap.Clear();
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

            if (editEvent is FileTransferStartEvent or FileChunkEvent or FileChunkAckEvent or FileTransferCancelEvent)
            {
                if (editEvent.ExecutorId == LocalUserId) return;

                if (editEvent is FileTransferStartEvent start) fileTransferManager.HandleTransferStart(start);
                else if (editEvent is FileChunkEvent chunk) fileTransferManager.HandleChunk(chunk);
                else if (editEvent is FileChunkAckEvent ack) fileTransferManager.HandleChunkAck(ack);
                else if (editEvent is FileTransferCancelEvent cancel) fileTransferManager.NoteCancelledByPeer(cancel.TransferId, cancel.CancelledBy);

                var executorId = editEvent.ExecutorId;
                dispatcher.InvokeAsync(() => UpdateParticipantActivity(executorId));
                return;
            }

            if (editEvent is RtcSignalEvent signal)
            {
                if (editEvent.ExecutorId != LocalUserId && editEvent.ExecutorId != Guid.Empty)
                {
                    if (webParticipants.ContainsKey(editEvent.ExecutorId)) previewStreams.HandleSignal(editEvent.ExecutorId, signal);
                    else peerTransport?.HandleSignal(editEvent.ExecutorId, signal);
                }
                return;
            }

            if (editEvent is ScriptEvent script)
            {
                HandleScript(script);
                return;
            }

            if (editEvent is CatchUpRequestEvent catchUpRequest)
            {
                if (editEvent.ExecutorId != LocalUserId && editEvent.ExecutorId != Guid.Empty) SendCatchUp(catchUpRequest);
                return;
            }

            if (editEvent is CatchUpEvent catchUp)
            {
                if (editEvent.ExecutorId != LocalUserId && editEvent.ExecutorId != Guid.Empty) ReceiveCatchUp(catchUp);
                return;
            }

            if (editEvent is ScriptListEvent scriptList)
            {
                if (scriptList.ExecutorId != LocalUserId && Participants.Any(p => p.UserId == scriptList.ExecutorId && p.Role == UserRole.Host))
                {
                    foreach (var item in scriptList.Scripts.Take(MaxScripts)) HandleScript(new ScriptEvent("add", item) { ExecutorId = item.AuthorId });
                }
                return;
            }

            if (editEvent is PreviewRequestEvent previewRequest)
            {
                if (editEvent.ExecutorId != LocalUserId && editEvent.ExecutorId != Guid.Empty) previewService.Request(previewRequest);
                return;
            }

            if (editEvent is PreviewFrameEvent or ItemSchemaEvent or ItemVisualsEvent or CreateItemResultEvent or MediaResultEvent or CatalogEvent) return;

            if (editEvent is ItemSchemaRequestEvent or CreateItemRequestEvent or ItemVisualsRequestEvent or MediaRequestEvent or CatalogRequestEvent)
            {
                if (editEvent.ExecutorId != LocalUserId && editEvent.ExecutorId != Guid.Empty) webAssist.Handle(editEvent);
                return;
            }

            if (editEvent is FileWantedEvent or FileOfferEvent or FilePullEvent)
            {
                if (editEvent.ExecutorId == LocalUserId || editEvent.ExecutorId == Guid.Empty) return;

                dispatcher.InvokeAsync(() => HandleFileSharingRequest(editEvent));
                return;
            }

            if (editEvent.ExecutorId == LocalUserId) return;

            if (SyncScenesTransfer.IsProjectEdit(editEvent) && HoldForCatchUp(editEvent)) return;

            if (dispatcher.CheckAccess())
            {
                ApplyEvent(editEvent);
                return;
            }

            bool schedule;
            lock (receivedEvents)
            {
                receivedEvents.Enqueue(editEvent);
                schedule = !receivedFlushScheduled;
                receivedFlushScheduled = true;
            }

            if (schedule) dispatcher.InvokeAsync(DrainReceivedEvents);
        }

        private readonly Queue<EditEvent> receivedEvents = new();
        private bool receivedFlushScheduled;

        private void DrainReceivedEvents()
        {
            try
            {
                while (true)
                {
                    EditEvent next;
                    bool more;
                    lock (receivedEvents)
                    {
                        if (receivedEvents.Count == 0) return;
                        next = receivedEvents.Dequeue();
                        more = receivedEvents.Count > 0;
                    }

                    try
                    {
                        ApplyEvent(next, suppressRedraw: more);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                bool rerun;
                lock (receivedEvents)
                {
                    rerun = receivedEvents.Count > 0;
                    receivedFlushScheduled = rerun;
                }

                if (rerun) Application.Current?.Dispatcher.InvokeAsync(DrainReceivedEvents);
            }
        }

        private void ApplyEvent(EditEvent editEvent, bool suppressRedraw = false)
        {
            var isReplay = editEvent is ReplayedEvent;
            if (editEvent is ReplayedEvent replayed) editEvent = replayed.Inner;

            if (!isReplay) UpdateParticipantActivity(editEvent.ExecutorId);

            if (!IsAllowedFrom(editEvent))
            {
                return;
            }

            if (SyncScenesTransfer.IsProjectEdit(editEvent))
            {
                if (sessionClient.Clock.Accept(editEvent) is not { } accepted) return;
                editEvent = accepted;
            }

            if (syncScenesTransfer.ShouldRelease) ReleaseHeldEvents();
            if (syncScenesTransfer.Hold(editEvent)) return;

            DispatchEvent(editEvent, suppressRedraw);
            if (!isReplay) NoteRemoteOperation(editEvent);
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

        private static readonly TimeSpan PlayingStaleAfter = TimeSpan.FromSeconds(3);

        private void ExpireStalePlayback()
        {
            var now = DateTime.UtcNow;
            foreach (var participant in Participants)
            {
                if (!participant.IsPlaying) continue;
                if (now - participant.FrameUpdatedAt < PlayingStaleAfter) continue;

                participant.IsPlaying = false;
            }
        }

        internal void HandleMissingResource(MissingResourceEvent evt)
        {
            ResourceAvailabilityChecker.NotifyReportedByPeer(
                GetParticipantName(evt.ExecutorId), evt.Fonts, evt.Plugins);
        }

        private void HandleUpdateAvailable(string? latestVersion)
        {
            if (updateNoticeShown) return;
            updateNoticeShown = true;

            Application.Current?.Dispatcher.InvokeAsync(() =>
                PromptUpdate(string.Format(Texts.NewVersionNextTime, ShownVersion(latestVersion))));
        }

        private static string ShownVersion(string? version) =>
            string.IsNullOrWhiteSpace(version) ? Texts.Unknown : version;

        private void HandleRoomNotFound(string? reason, string? latestVersion)
        {
            var refusedRoomId = RoomId;
            if (reason == "kept_limit") Settings.MultiUserEditSettings.Default.SetKeptRoomKey(refusedRoomId, null);
            var refusedAsOwner = reason == "host_key"
                && localUserRole == UserRole.Host
                && isKeptRoom;
            var missingOnRelay = reason == "missing"
                && localUserRole == UserRole.Guest
                && !sessionClient.UsesBackupServer;

            guestRoomValidation?.TrySetResult(false);
            ResetNetworkState();

            if (refusedAsOwner || missingOnRelay)
            {
                Application.Current?.Dispatcher.InvokeAsync(async () =>
                {
                    InputRoomId = refusedRoomId;
                    await JoinRoomAsync(asOwnerIfPossible: false, useBackupServer: missingOnRelay);
                });
                return;
            }

            relayFailureDetail = null;

            if (reason == "outdated")
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    PromptUpdate(string.Format(Texts.OutdatedPlugin, UpdateChecker.Instance.DisplayVersion, ShownVersion(latestVersion))));
                return;
            }

            if (reason == "full")
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(Texts.RoomFull, Texts.RoomFullTitle, MessageBoxButton.OK));
                return;
            }

            var message = reason switch
            {
                "expired" => Texts.RoomExpired,
                "host_key" => Texts.RoomHostKeyRejected,
                "identity" => Texts.RoomIdentityRejected,
                "rate_limited" => Texts.RoomRateLimited,
                "too_many_joins" => Texts.RoomJoinRateLimited,
                "kept_limit" => Texts.RoomKeptLimit,
                "closed" => Texts.HostClosedRoom,
                _ => Texts.RoomNotFound
            };

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, Texts.ConnectionError, MessageBoxButton.OK);
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
                peerFileChannels.TryRemove(participant.UserId, out _);
                directPeers.TryRemove(participant.UserId, out _);
                peerTransport?.Forget(participant.UserId);
                previewStreams.Forget(participant.UserId);
                permissionOverrides.TryRemove(participant.UserId, out _);
            }
        }

        internal void HandleUserKickedEvent(UserKickedEvent evt)
        {
            if (!IsFromHost(evt.ExecutorId)) return;

            if (evt.TargetUserId == LocalUserId)
            {
                Application.Current?.Dispatcher.InvokeAsync(async () =>
                {
                    MessageBox.Show(Texts.KickedMessage, Texts.KickedTitle, MessageBoxButton.OK);
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
                        peerFileChannels.TryRemove(p.UserId, out _);
                        directPeers.TryRemove(p.UserId, out _);
                        peerTransport?.Forget(p.UserId);
                        previewStreams.Forget(p.UserId);
                        permissionOverrides.TryRemove(p.UserId, out _);
                    }
                });
            }
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
                    var stateSource = PermissionOf(evt.UserId).CanEditProperties ? evt.UserId : CurrentHostId;
                    if (stateSource != Guid.Empty && stateSource != LocalUserId) RequestItemState(evt.ItemId, stateSource);
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

        internal void HandleCharacterRequest(CharacterRequestEvent evt) => characterShareManager.HandleRequest(evt);

        internal void HandleCharacterShared(CharacterSharedEvent evt) => characterShareManager.HandleShared(evt);

        internal static bool IsCharacterDecided(string characterName) => CharacterShareManager.IsDecided(characterName);

        internal void RequestCharacter(string characterName, Guid ownerId, Action resume)
        {
            characterShareManager.WhenDecided(characterName, resume);
            characterShareManager.RequestIfNeeded(characterName, ownerId);
        }

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
                    catch
                    {
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

        public void NotifyPlaybackStateChanged()
        {
            if (!IsConnected || FirstOrDefaultTimeline == null) return;

            SendCursorMovedAsync(FirstOrDefaultTimeline.CurrentFrame, force: true);
        }

        public void SendCursorMovedAsync(int currentFrame) => SendCursorMovedAsync(currentFrame, force: false);

        public async void SendCursorMovedAsync(int currentFrame, bool force)
        {
            try
            {
                var isPlaying = getIsPlayingFunc?.Invoke() ?? false;
                if (!force
                    && !isPlaying
                    && currentFrame == lastAppliedRemoteFrame
                    && DateTime.UtcNow - lastAppliedRemoteFrameAt < CursorEchoWindow) return;

                var timelineIndex = Scenes?.Timelines.IndexOf(FirstOrDefaultTimeline!) ?? 0;
                await eventSender.SendCursorMovedThrottledAsync(currentFrame, timelineIndex, isPlaying);
            }
            catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            disposer.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
