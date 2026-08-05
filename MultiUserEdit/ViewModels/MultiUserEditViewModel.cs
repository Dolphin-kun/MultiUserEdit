using MultiUserEdit.Commons;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.UndoRedo;
using Newtonsoft.Json;

namespace MultiUserEdit.ViewModels
{
    public class MultiUserEditViewModel : Bindable, ITimelineToolViewModel, IDisposable
    {
        private readonly SessionClient sessionClient;
        private readonly ClientEventDispatcher eventDispatcher;
        private readonly AdornerManager adornerManager;
        public AdornerManager AdornerManager => adornerManager;
        private readonly FileTransferManager fileTransferManager;
        private readonly EditEventSender eventSender;
        internal EditEventSender EventSender => eventSender;
        private readonly TimelineSyncManager timelineSyncManager;
        internal TimelineSyncManager TimelineSyncManager => timelineSyncManager;
        private readonly System.Windows.Threading.DispatcherTimer presenceTimer;
        private bool isConnected;
        private bool disposed;
        private bool isApplyingRemoteEvent;

        private UndoRedoManager? undoRedoManager;
        public Scenes? Scenes { get; private set; }
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

        public ICommand CreateRoomCommand { get; }
        public ICommand JoinRoomCommand { get; }
        public ICommand CopyRoomIdCommand { get; }
        public ICommand CopyInviteLinkCommand { get; }
        public ICommand JoinFromClipboardCommand { get; }
        public ICommand RegisterProtocolCommand { get; }
        public ICommand UnregisterProtocolCommand { get; }
        public ICommand DisconnectCommand { get; }

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
                var cleanId = ExtractRoomId(value);
                Set(ref inputRoomId, cleanId);
                (JoinRoomCommand as ActionCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string UserName
        {
            get => Settings.MultiUserEditSettings.Default.UserName;
            set
            {
                Settings.MultiUserEditSettings.Default.UserName = value;
                OnPropertyChanged(nameof(UserName));
            }
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

        public MultiUserEditViewModel()
        {
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
            sessionClient.ConnectionStateChanged += HandleConnectionStateChanged;

            CreateRoomCommand = new ActionCommand(_ => !IsConnected, async _ => await CreateRoomAsync());
            JoinRoomCommand = new ActionCommand(_ => !IsConnected && !string.IsNullOrWhiteSpace(InputRoomId), async _ => await JoinRoomAsync());
            CopyRoomIdCommand = new ActionCommand(_ => !string.IsNullOrEmpty(RoomId), _ => Clipboard.SetText(RoomId));
            CopyInviteLinkCommand = new ActionCommand(_ => !string.IsNullOrEmpty(RoomId), _ => Clipboard.SetText($"https://multi-user-edit.dolphin-discord-js.workers.dev/?roomId={RoomId}"));
            JoinFromClipboardCommand = new ActionCommand(_ => !IsConnected, async _ =>
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    var extracted = ExtractRoomId(text);
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
                    MessageBox.Show($"Webディープリンク (ymm4-multi-user-edit://) をWindowsに登録しました。", "登録完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
            UnregisterProtocolCommand = new ActionCommand(_ => true, _ =>
            {
                if (ProtocolRegister.UnregisterCustomProtocol())
                {
                    IsProtocolRegistered = false;
                    MessageBox.Show($"Webディープリンク (ymm4-multi-user-edit://) の登録を削除・解除しました。", "解除完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
            DisconnectCommand = new ActionCommand(_ => IsConnected, async _ => await StopNetworkAsync());

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

        private void CheckCommandLineArgsForDeepLink()
        {
            try
            {
                var targetArg = GetDeepLinkTargetArgument();
                if (string.IsNullOrEmpty(targetArg)) return;

                var extracted = ExtractRoomId(targetArg);
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

        private static bool ExecuteCreateNewWindowCommand()
        {
            try
            {
                var cmd = CommandSettings.Default[CommandType.CreateNewWindow];
                var mainWindow = Application.Current.MainWindow;
                if (cmd != null && cmd.CanExecute(null, mainWindow))
                {
                    cmd.Execute(null, mainWindow);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] ExecuteCreateNewWindowCommand failed: {ex.Message}");
            }
            return false;
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

        private void PresenceTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.Now;
            foreach (var p in Participants)
            {
                if (p.UserId == LocalUserId) continue;
                if (now - p.LastActivity > TimeSpan.FromMinutes(3))
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

        public void SetTimelineToolInfo(TimelineToolInfo info)
        {
            undoRedoManager?.UndoRedoCommandCreated -= OnUndoRedoCommandCreated;
            if (Scenes is not null)
            {
                UnsubscribeScenesEvents();
                foreach (var timeline in Scenes.Timelines) timelineSyncManager.UnregisterTimeline(timeline);
            }

            undoRedoManager = info.UndoRedoManager;
            Scenes = info.Scenes;
            FirstOrDefaultTimeline = Scenes?.Timelines.FirstOrDefault(t => t.ID == info.Timeline?.ID) ?? Scenes?.Timelines.FirstOrDefault();

            undoRedoManager?.UndoRedoCommandCreated += OnUndoRedoCommandCreated;

            if (Scenes is not null)
            {
                SubscribeScenesEvents();
                foreach (var timeline in Scenes.Timelines) timelineSyncManager.RegisterTimeline(timeline, this);
                adornerManager.AttachAdorner(this);
            }
            else
            {
                adornerManager.DetachAdorner();
            }

            if (IsConnected && FirstOrDefaultTimeline != null)
                SendCursorMovedAsync(FirstOrDefaultTimeline.CurrentFrame);

            RefreshCommandStates();
        }

        private void SubscribeScenesEvents()
        {
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

            for (int i = 0; i < currentTimelines.Count; i++)
            {
                var t = currentTimelines[i];
                if (!scenesSnapshot.Contains(t))
                {
                    timelineSyncManager.RegisterTimeline(t, this);
                    SendSceneAddedAsync(i, t.Name);
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

        private void RefreshCommandStates()
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

                                ItemIdManager.RegisterId(item, onlineItem.ItemId);
                                timeline.TryAddItems([item], onlineItem.Frame, onlineItem.Layer, false);
                                timelineSyncManager.UpdateItemJsonCache(item);
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
                adornerManager.AttachAdorner(this);
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

        private async Task ConnectNetworkAsync(string targetRoomId)
        {
            try
            {
                await sessionClient.StartAsync(targetRoomId);
                Participants.Clear();
                Participants.Add(new Participant
                {
                    UserId = LocalUserId,
                    UserName = UserName,
                    Role = localUserRole,
                    Permission = CurrentUserPermission,
                    LastActivity = DateTime.Now,
                    Status = UserStatus.Active,
                    ThemeColor = GenerateParticipantColor(LocalUserId)
                });

                var presenceEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, false)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, presenceEvt);

                if (localUserRole == UserRole.Guest)
                {
                    await Task.Delay(500);
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
        }

        private async Task StopNetworkAsync()
        {
            try
            {
                if (IsConnected)
                {
                    // 退室通知（ホスト退出またはゲスト退出）を他メンバーへ通知
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
                ThemeColor = GenerateParticipantColor(userId),
                Status = UserStatus.Active
            };

            Participants.Add(p);
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
                    UserName = evt.UserName,
                    Role = evt.Role,
                    LastActivity = DateTime.Now,
                    ThemeColor = GenerateParticipantColor(evt.UserId),
                    Status = UserStatus.Active
                };
                Participants.Add(p);
            }
            else
            {
                p.UserName = evt.UserName;
                p.Role = evt.Role;
                p.LastActivity = DateTime.Now;
                p.Status = UserStatus.Active;
            }

            if (!evt.IsReply)
            {
                var replyEvt = new PresenceEvent(LocalUserId, UserName, localUserRole, true)
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
                    var itemJson = JsonConvert.SerializeObject(item, itemType, ItemSerializerOptions.Default);
                    onlineTimeline.Items.Add(new OnlineItem
                    {
                        ItemId = ItemIdManager.GetOrCreateId(item),
                        ItemTypeName = itemType.AssemblyQualifiedName ?? itemType.FullName ?? "",
                        ItemJson = itemJson,
                        Frame = item.Frame,
                        Layer = item.Layer
                    });
                }
                onlineScenes.Timelines.Add(onlineTimeline);
            }

            var syncEvent = new SyncScenesEvent(onlineScenes)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            };
            _ = sessionClient.SendAsync(null, syncEvent);
        }

        private void DispatchEvent(EditEvent editEvent)
        {
            isApplyingRemoteEvent = true;
            try
            {
                eventDispatcher.Dispatch(editEvent, this);
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

        private void HandleConnectionStateChanged(bool connected)
        {
            if (!connected)
            {
                ResetNetworkState();
            }
            else
            {
                IsConnected = true;
                RefreshCommandStates();
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

        private async void SendCursorMovedAsync(int currentFrame)
        {
            try
            {
                var timelineIndex = Scenes?.Timelines.IndexOf(FirstOrDefaultTimeline!) ?? 0;
                var isPlaying = getIsPlayingFunc?.Invoke() ?? false;
                var evt = new CursorMovedEvent(currentFrame, timelineIndex, isPlaying)
                {
                    DateTime = DateTime.UtcNow,
                    ExecutorId = LocalUserId
                };
                await sessionClient.SendAsync(null, evt);
            }
            catch { }
        }

        private static System.Windows.Media.Color GenerateParticipantColor(Guid userId)
        {
            return ParticipantColorGenerator.Generate(userId);
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
                    timelineSyncManager.UpdateItemJsonCache(item);
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

        internal void UpdateItemJsonCache(IItem item)
        {
            timelineSyncManager.UpdateItemJsonCache(item);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            presenceTimer.Stop();
            presenceTimer.Tick -= PresenceTimer_Tick;

            FirstOrDefaultTimeline = null;

            sessionClient.EventReceived -= HandleEventReceived;
            sessionClient.Disconnected -= HandleDisconnected;
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