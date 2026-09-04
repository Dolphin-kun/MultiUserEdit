using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using MultiUserEdit.ViewModels;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        private readonly Dictionary<Guid, long> locallyLockedItems = [];
        private readonly Dictionary<Guid, DateTime> lockedItemsReceivedAt = [];

        public event Action<string, string>? FileTransferCompleted;

        private Func<int, bool, Task>? previewSeekAction;
        private Func<bool>? getIsPlayingFunc;
        public void SetPreviewSeekAction(Func<int, bool, Task> action, Func<bool>? isPlayingFunc = null)
        {
            previewSeekAction = action;
            getIsPlayingFunc = isPlayingFunc;
        }

        public ObservableCollection<string> ReceivedMessages { get; } = [];
        public ObservableCollection<Participant> Participants { get; } = [];

        // プロジェクトへ保存された、プロファイルIDごとの合計参加時間
        private ProjectParticipationState participationState = new();

        // ホストが常に一覧の先頭へ来るように挿入する
        private void AddParticipantSorted(Participant participant)
        {
            var index = participant.Role == UserRole.Host ? 0 : Participants.Count;
            Participants.Insert(index, participant);
        }

        private void MoveHostToTopIfNeeded(Participant participant)
        {
            if (participant.Role != UserRole.Host) return;

            var index = Participants.IndexOf(participant);
            if (index > 0) Participants.Move(index, 0);
        }

        // 経過時間を合計へ確定し、計測の起点を現在時刻へ戻す（複数回保存しても二重加算されない）
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

        // 保存済みの合計に、現在のセッションでの経過時間を加えた値
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
        public ICommand KickUserCommand { get; }

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
                if (Settings.MultiUserEditSettings.Default.UserDescription != value)
                {
                    Settings.MultiUserEditSettings.Default.UserDescription = value;
                    OnPropertyChanged(nameof(UserDescription));

                    var localParticipant = Participants.FirstOrDefault(p => p.UserId == LocalUserId);
                    localParticipant?.Description = value;

                    BroadcastLocalPresence();
                }
            }
        }

        // インストール単位で不変のID。合計参加時間を同一人物として積算するためのキー。
        public static Guid LocalProfileId =>
            Guid.TryParse(Settings.MultiUserEditSettings.Default.ProfileId, out var id) ? id : Guid.Empty;

        private void BroadcastLocalPresence()
        {
            if (!IsConnected) return;

            var presenceEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, true, LocalProfileId, UserDescription)
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
            fileTransferManager = new FileTransferManager();
            eventSender = new EditEventSender(sessionClient, fileTransferManager, () => LocalUserId);
            timelineSyncManager = new TimelineSyncManager(eventSender, () => isApplyingRemoteEvent);

            fileTransferManager.TransferCompleted += (transferId, path) => FileTransferCompleted?.Invoke(transferId, path);
            fileTransferManager.TransferSummaryChanged += OnTransferSummaryChanged;

            LocalUserId = sessionClient.LocalUserId;

            sessionClient.EventReceived += HandleEventReceived;
            sessionClient.Disconnected += HandleDisconnected;
            sessionClient.RoomNotFound += HandleRoomNotFound;
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
                    MessageBox.Show($"Webディープリンク (ymm4-multi-user-edit://) の登録を削除・解除しました。", "解除完了", MessageBoxButton.OK);
                }
            });
            DisconnectCommand = new ActionCommand(_ => IsConnected, async _ => await StopNetworkAsync());
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
            if (Application.Current != null)
            {
                Application.Current.Exit += Application_Exit;
            }

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
                    _ = sessionClient.SendAsync(null, evt);
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

            // YMM4は多重起動できないため、既に起動中の状態でリンクを開いた場合はランチャー(MultiUserEditLauncher.exe)が
            // 一時ファイルにディープリンクを書き出した上でYMM4を無引数起動する（詳細はProtocolRegister参照）。
            // その場合、コマンドライン引数ではなくこちらから拾う。
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

        private void OnTransferSummaryChanged(TransferSummary summary)
        {
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                if (!summary.IsActive)
                {
                    if (IsTransferring)
                    {
                        TransferProgress = 100.0;
                        await Task.Delay(1000);
                        IsTransferring = false;
                        TransferProgress = 0;
                        TransferStatusText = string.Empty;
                    }
                    return;
                }

                IsTransferring = true;
                TransferStatusText = summary.DisplayText;

                if (summary.OverallProgress > TransferProgress || summary.OverallProgress >= 100.0)
                {
                    TransferProgress = summary.OverallProgress;
                }
            });
        }

        // この時間だけ操作（イベントの送受信）が無ければ離席中とみなす
        private static readonly TimeSpan AwayThreshold = TimeSpan.FromMinutes(3);

        private void PresenceTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.Now;
            foreach (var p in Participants)
            {
                if (p.UserId == LocalUserId)
                {
                    // 自分自身も他の参加者から見えている状態と同じ基準（イベント送信の有無）で判定する
                    p.LastActivity = sessionClient.LastSentAt;
                    p.Status = now - p.LastActivity > AwayThreshold ? UserStatus.Away : UserStatus.Active;
                    continue;
                }

                if (now - p.LastActivity > AwayThreshold)
                    p.Status = UserStatus.Away;
            }

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
                isApplyingRemoteEvent = true;
                try
                {
                    foreach (var t in newlyAdded)
                    {
                        Scenes.DeleteScene(t);
                    }
                }
                finally { isApplyingRemoteEvent = false; }
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
            isApplyingRemoteEvent = true;
            try
            {
                action();
            }
            finally
            {
                isApplyingRemoteEvent = false;
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
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        public void ApplySyncScenes(OnlineScenes onlineScenes)
        {
            if (onlineScenes == null || Scenes == null) return;

            if (Scenes.Timelines.Any(t => t.Items.Count > 0))
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

            isApplyingRemoteEvent = true;
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

                    if (timeline.Items.Count > 0)
                        timeline.DeleteItems([.. timeline.Items]);

                    foreach (var onlineItem in onlineTimeline.Items)
                    {
                        var itemType = Type.GetType(onlineItem.ItemTypeName);
                        if (itemType == null) continue;

                        try
                        {
                            if (JsonConvert.DeserializeObject(onlineItem.ItemJson, itemType, ItemSerializerOptions.Default) is IItem item)
                            {
                                CharacterResolver.TryResolveCharacter(item);

                                if (onlineItem.MediaFileNames is { Count: > 0 })
                                {
                                    var requiresRealContainer = MediaFileResolver.RequiresRealMediaContainer(item);

                                    foreach (var mediaFileName in onlineItem.MediaFileNames)
                                    {
                                        var targetSavePath = MediaFileResolver.ResolveLocalTempPath(mediaFileName);

                                        if (requiresRealContainer)
                                        {
                                            MediaFileResolver.ClearRealMediaFilePath(item);
                                        }
                                        else
                                        {
                                            MediaFileResolver.EnsurePlaceholderFile(targetSavePath);
                                            MediaFileResolver.ReplaceFilePath(item, mediaFileName, targetSavePath);
                                        }

                                        // 動画・音声以外は初回のReplaceFilePathで既に正しい最終パスになっている
                                        // （転送完了時は中身が差し替わるだけでパス自体は変わらない）ため、
                                        // 動画・音声（転送完了まで参照をnullにしている）の場合だけ完了を待つ
                                        if (requiresRealContainer)
                                        {
                                            var fileName = Path.GetFileName(targetSavePath);

                                            void onCompleted(string transferId, string savedPath)
                                            {
                                                if (Path.GetFileName(savedPath).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    FileTransferCompleted -= onCompleted;
                                                    Application.Current?.Dispatcher.InvokeAsync(() =>
                                                    {
                                                        ExecuteRemoteAction(() => MediaFileResolver.SetFilePath(item, savedPath));
                                                    });
                                                }
                                            }

                                            FileTransferCompleted += onCompleted;
                                        }
                                    }
                                }

                                ItemIdManager.RegisterId(item, onlineItem.ItemId);
                                timeline.TryAddItems([item], onlineItem.Frame, onlineItem.Layer, false);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"[Sync Error]\n{ex.Message}\nType: {onlineItem.ItemTypeName}");
                        }
                    }
                }

                scenesSnapshot = [.. Scenes.Timelines];
            }
            finally
            {
                isApplyingRemoteEvent = false;
                if (activeViewModel != null)
                {
                    adornerManager.AttachAdorner(activeViewModel);
                }
            }
        }

        private async Task CreateRoomAsync()
        {
            RoomId = Guid.NewGuid().ToString();
            localUserRole = UserRole.Host;
            IsHost = true;
            CurrentUserPermission = UserPermission.CreateFromLevel(PermissionLevel.Full);
            await ConnectNetworkAsync(RoomId);
        }

        private async Task JoinRoomAsync()
        {
            RoomId = InputRoomId;
            localUserRole = UserRole.Guest;
            IsHost = false;
            await ConnectNetworkAsync(RoomId);
        }

        // Guest接続時、サーバーからの room_not_found 通知（HandleRoomNotFound）を
        // 一定時間待ってから「参加済み」のUI状態（Participants・IsConnected等）を反映する。
        // これにより、実際には参加できていないルームが一瞬でも「接続済み」に見えてしまうのを防ぐ。
        private TaskCompletionSource<bool>? guestRoomValidation;
        private bool isJoining;

        private async Task ConnectNetworkAsync(string targetRoomId)
        {
            isJoining = true;
            RefreshCommandStates();
            try
            {
                if (localUserRole == UserRole.Guest)
                {
                    guestRoomValidation = new TaskCompletionSource<bool>();
                }

                await sessionClient.StartAsync(targetRoomId, localUserRole == UserRole.Host);

                if (localUserRole == UserRole.Guest)
                {
                    var validation = guestRoomValidation!;
                    var winner = await Task.WhenAny(validation.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
                    var roomIsValid = winner != validation.Task || validation.Task.Result;
                    guestRoomValidation = null;

                    if (!roomIsValid || !sessionClient.IsConnected) return;

                    IsConnected = true;
                    RefreshCommandStates();
                }

                Participants.Clear();
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

                var presenceEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, false, LocalProfileId, UserDescription)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, presenceEvt);

                if (localUserRole == UserRole.Guest)
                {
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
                isJoining = false;
                RefreshCommandStates();
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
            void ClearState()
            {
                IsConnected = false;
                IsHost = false;
                RoomId = string.Empty;
                InputRoomId = string.Empty;
                Participants.Clear();
                locallyLockedItems.Clear();
                lockedItemsReceivedAt.Clear();
                LockedItems.Clear();
                fileTransferManager.CancelAll();
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

            void Process()
            {
                if (editEvent.ExecutorId == LocalUserId) return;
                UpdateParticipantActivity(editEvent.ExecutorId);
                DispatchEvent(editEvent);
            }

            if (dispatcher.CheckAccess())
                Process();
            else
                dispatcher.Invoke(Process);
        }

        private void UpdateParticipantActivity(Guid executorId)
        {
            if (executorId == Guid.Empty || executorId == LocalUserId) return;
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
                    Description = evt.Description,
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
                p.Description = evt.Description;
                p.Role = evt.Role;
                p.LastActivity = DateTime.Now;
                p.Status = UserStatus.Active;
                if (evt.ProfileId != Guid.Empty) p.ProfileId = evt.ProfileId;
                MoveHostToTopIfNeeded(p);
            }

            if (!evt.IsReply)
            {
                var replyEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, true, LocalProfileId, UserDescription)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                _ = sessionClient.SendAsync(null, replyEvt);

                if (FirstOrDefaultTimeline != null)
                    SendCursorMovedAsync(FirstOrDefaultTimeline.CurrentFrame);
            }
        }

        internal void HandleSyncRequestEvent(SyncRequestEvent evt)
        {
            if (localUserRole != UserRole.Host || Scenes == null) return;

            var onlineScenes = new OnlineScenes();
            var filesToTransfer = new List<string>();
            foreach (var timeline in Scenes.Timelines)
            {
                var onlineTimeline = new OnlineTimeline
                {
                    Id = timeline.ID,
                    Name = timeline.Name,
                    Length = timeline.Length
                };
                foreach (var item in timeline.Items)
                {
                    var itemType = item.GetType();
                    var (itemJson, files) = MediaFileResolver.SerializeForSync(item, itemType);
                    onlineTimeline.Items.Add(new OnlineItem
                    {
                        ItemId = ItemIdManager.GetOrCreateId(item),
                        ItemTypeName = itemType.AssemblyQualifiedName ?? itemType.FullName ?? "",
                        ItemJson = itemJson,
                        MediaFileNames = files.Count > 0 ? files.Select(fp => Path.GetFileName(fp)!).ToList() : null,
                        Frame = item.Frame,
                        Layer = item.Layer
                    });
                    filesToTransfer.AddRange(files);
                }
                onlineScenes.Timelines.Add(onlineTimeline);
            }

            var syncEvent = new SyncScenesEvent(onlineScenes)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            _ = sessionClient.SendAsync(null, syncEvent);

            // 参加時点で既にタイムライン上にある素材の実データを新規参加者へ転送する
            // （SyncScenesEventはプレースホルダーの参照情報のみを含み、実バイト列は別途送る必要がある）
            foreach (var filePath in filesToTransfer.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _ = fileTransferManager.TransferAsync(filePath, sessionClient, LocalUserId);
            }
        }

        private void DispatchEvent(EditEvent editEvent)
        {
            if (activeViewModel == null) return;
            isApplyingRemoteEvent = true;
            try
            {
                eventDispatcher.Dispatch(editEvent, activeViewModel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] dispatch failed: {ex.Message}");
            }
            finally
            {
                isApplyingRemoteEvent = false;
            }
        }

        private void HandleDisconnected()
        {
            Debug.WriteLine("[MultiUserEdit] Disconnected by server");
            ResetNetworkState();
        }

        private void HandleRoomNotFound()
        {
            guestRoomValidation?.TrySetResult(false);
            ResetNetworkState();

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show("ルームが存在しません。ルームIDを確認してください。", "参加エラー", MessageBoxButton.OK);
            });
        }

        private void HandleConnectionStateChanged(bool connected)
        {
            if (!connected)
            {
                ResetNetworkState();
                return;
            }

            // Guestの場合、room_not_found の判定が終わるまでは ConnectNetworkAsync 側で
            // IsConnected を立てる（参加者一覧・切断ボタン等をそれまで「未接続」に見せるため）。
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

        // アプリ側の離脱通知（UserLeftEvent）を送れないまま切断された（YMM4終了・クラッシュ・回線切断等）場合に
        // サーバーが代わりに通知してくる peer_disconnected を、通常の離脱処理にそのまま合流させる。
        private void HandlePeerDisconnected(Guid userId, bool isHost)
        {
            HandleUserLeftEvent(new UserLeftEvent(userId, isHost)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = userId
            });
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
                }
                return;
            }

            LockedItems[evt.ItemId] = evt.UserId;
            lockedItemsReceivedAt[evt.ItemId] = DateTime.UtcNow;
        }

        internal void HandleItemUnlockedEvent(ItemUnlockedEvent evt)
        {
            LockedItems.Remove(evt.ItemId);
            lockedItemsReceivedAt.Remove(evt.ItemId);
        }

        internal void HandleCursorMovedEvent(CursorMovedEvent evt)
        {
            if (evt.ExecutorId == Guid.Empty || evt.ExecutorId == LocalUserId) return;
            var p = EnsureParticipant(evt.ExecutorId);
            p.CurrentFrame = evt.CurrentFrame;
            p.CurrentTimelineIndex = evt.TimelineIndex;

            if (!IsHost && CurrentUserPermission.CanSyncSeekPosition && p.Role == UserRole.Host)
            {
                _ = SyncPlaybackStateAsync(evt.CurrentFrame, evt.IsPlaying);
            }
        }

        private async Task SyncPlaybackStateAsync(int frame, bool isPlaying)
        {
            isApplyingRemoteEvent = true;
            try
            {
                if (!isPlaying && FirstOrDefaultTimeline != null)
                {
                    FirstOrDefaultTimeline.UnlockCurrentFrame();
                    FirstOrDefaultTimeline.CurrentFrame = frame;
                }

                if (previewSeekAction != null)
                {
                    await previewSeekAction(frame, isPlaying);
                }
            }
            finally
            {
                isApplyingRemoteEvent = false;
            }
        }

        internal void HandleFileTransferStart(FileTransferStartEvent evt)
        {
            fileTransferManager.HandleTransferStart(evt, LocalUserId);
        }

        internal void HandleFileChunk(FileChunkEvent evt)
        {
            fileTransferManager.HandleChunk(evt, LocalUserId);
        }

        public Task SendFileAsync(string filePath) =>
            fileTransferManager.SendFileAsync(filePath, sessionClient, LocalUserId);

        private void ForceDeselectItem(Guid itemId)
        {
            if (Scenes == null) return;

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
                isApplyingRemoteEvent = false;
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
                var timelineIndex = Scenes?.Timelines.IndexOf(FirstOrDefaultTimeline!) ?? 0;
                var isPlaying = getIsPlayingFunc?.Invoke() ?? false;
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
            locallyLockedItems.Remove(itemId);
            _ = eventSender.SendItemUnlockAsync(itemId);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            AppDomain.CurrentDomain.ProcessExit -= CurrentDomain_ProcessExit;
            if (Application.Current != null)
            {
                Application.Current.Exit -= Application_Exit;
            }

            presenceTimer.Stop();
            presenceTimer.Tick -= PresenceTimer_Tick;

            FirstOrDefaultTimeline = null;

            sessionClient.EventReceived -= HandleEventReceived;
            sessionClient.Disconnected -= HandleDisconnected;
            sessionClient.RoomNotFound -= HandleRoomNotFound;
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
