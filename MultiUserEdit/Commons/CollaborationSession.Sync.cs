using MultiUserEdit.Commons.EventHandlers;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.Commons.Models;
using MultiUserEdit.Networking;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    public partial class CollaborationSession
    {
        private void ApplySyncScenes(OnlineScenes onlineScenes, Guid ownerId, bool isManual)
        {
            if (onlineScenes == null || Scenes == null) return;

            if (isApplyingSyncScenes)
            {
                return;
            }

            var showedSceneTabs = SceneTabRefresher.ShowsSceneTabs(Scenes);

            isApplyingSyncScenes = true;
            try
            {
                ApplySyncScenesCore(onlineScenes, ownerId, isManual);
            }
            finally
            {
                isApplyingSyncScenes = false;
            }

            if (showedSceneTabs != SceneTabRefresher.ShowsSceneTabs(Scenes)) RequestSceneTabRefresh();
        }

        private void ApplySyncScenesCore(OnlineScenes onlineScenes, Guid ownerId, bool isManual)
        {

            if (isManual)
            {
                var requestedByMe = pendingManualSyncSource == ownerId;
                if (requestedByMe) pendingManualSyncSource = null;

                if (!requestedByMe && Scenes.Timelines.Any(t => t.Items.Count > 0))
                {
                    var sourceName = GetParticipantName(ownerId);
                    var accepted = MessageBox.Show(
                        string.Format(Texts.PeerSyncedData, sourceName) + "\n\n" + SyncFromConfirmMessage(sourceName),
                        Texts.SyncData,
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
                    Texts.InitialSyncConfirm,
                    Texts.InitialSyncTitle,
                    MessageBoxButton.OKCancel);

                if (result != MessageBoxResult.OK)
                {
                    Application.Current?.Dispatcher.InvokeAsync(async () => await StopNetworkAsync());
                    return;
                }
            }

            RememberLocalOriginals();
            eventSender.ClearAllBaselines();
            SceneIdMap.Clear();

            var previousApplying = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            using var undoScope = UndoRecordSuppressor.Suppress(undoRedoManager);
            try
            {
                var hostSceneIds = onlineScenes.Timelines.Select(t => t.Id).Where(id => id != Guid.Empty).ToHashSet();
                var claimed = new HashSet<Timeline>(ReferenceEqualityComparer.Instance);
                var ordered = new List<Timeline>(onlineScenes.Timelines.Count);

                for (int i = 0; i < onlineScenes.Timelines.Count; i++)
                {
                    var onlineTimeline = onlineScenes.Timelines[i];
                    var timeline = Scenes.Timelines.FirstOrDefault(t => t.ID == onlineTimeline.Id && !claimed.Contains(t));

                    if (timeline == null
                        && Scenes.Timelines.ElementAtOrDefault(i) is { } candidate
                        && !claimed.Contains(candidate)
                        && !hostSceneIds.Contains(candidate.ID))
                    {
                        timeline = candidate;
                    }

                    timeline ??= CreateSceneForSync(onlineTimeline, Scenes.Timelines.Count);

                    if (timeline == null)
                    {
                        ErrorNotifier.NotifyList(
                            Texts.SceneUnavailableTitle,
                            Texts.SceneUnavailableLead,
                            ErrorNotifier.Quote(onlineTimeline.Name),
                            Texts.SceneUnavailableNote);
                        continue;
                    }

                    claimed.Add(timeline);
                    ordered.Add(timeline);
                    SceneIdMap.Remember(onlineTimeline.Id, timeline.ID);

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

                if (ordered.Count > 0 && !Scenes.Timelines.SequenceEqual(ordered))
                {
                    try
                    {
                        Scenes.Timelines = [.. ordered];
                    }
                    catch
                    {
                    }
                }

                TrackRemoteSceneChanges();
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

        private Timeline? CreateSceneForSync(OnlineTimeline onlineTimeline, int index)
        {
            if (Scenes == null) return null;

            try
            {
                var videoInfo = new VideoInfo();
                if (FirstOrDefaultTimeline?.VideoInfo is { } source)
                {
                    VideoInfoSerializer.Apply(videoInfo, source.Width, source.Height, source.FPS, source.Hz, null);
                    videoInfo.BackgroundColor = source.BackgroundColor;
                }

                var name = string.IsNullOrWhiteSpace(onlineTimeline.Name) ? string.Format(Texts.SceneDefaultName, index + 1) : onlineTimeline.Name;
                var sceneId = onlineTimeline.Id == Guid.Empty ? (Guid?)null : onlineTimeline.Id;

                Scenes.CreateScene(name, index, videoInfo, new TimelineVerticalLine(), sceneId);

                return (sceneId != null ? Scenes.Timelines.FirstOrDefault(t => t.ID == sceneId) : null)
                    ?? Scenes.Timelines.ElementAtOrDefault(index);
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not create a scene while syncing", ex);
                return null;
            }
        }

        private void AddSyncedItem(Timeline timeline, OnlineItem onlineItem, Guid ownerId) =>
            AddSyncedItem(timeline, onlineItem, ownerId, canWait: true);

        private void AddSyncedItem(Timeline timeline, OnlineItem onlineItem, Guid ownerId, bool canWait)
        {
            var itemType = ItemTypeResolver.Resolve(onlineItem.ItemTypeName);
            if (itemType != null && !typeof(IItem).IsAssignableFrom(itemType)) return;
            if (itemType == null)
            {
                RenderReadiness.MarkMissing(onlineItem.ItemId, TimelineIndexOf(timeline));
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

                        foreach (var name in pending)
                        {
                            ErrorNotifier.NotifyList(
                                Texts.MediaNotReceivedTitle,
                                string.Format(Texts.MediaNotReceivedLead, GetParticipantName(ownerId)),
                                ErrorNotifier.Quote(name),
                                Texts.ResyncToReceiveNote);
                        }
                        return;
                    }
                }

                var itemJson = LocalMediaGuard.StripVoiceCache(
                    ItemJsonSanitizer.RemoveUnknownEnumValues(
                        MediaFileResolver.ResolveJsonFileReferences(onlineItem.ItemJson, itemType, onlineItem.MediaFileNames)));

                if (JsonConvert.DeserializeObject(itemJson, itemType, ItemSerializerOptions.Default) is not IItem item) return;

                CharacterResolver.TryResolveCharacter(item);

                ItemIdManager.RegisterId(item, onlineItem.ItemId);
                timeline.TryAddItems([item], onlineItem.Frame, onlineItem.Layer, false);
                RenderReadiness.MarkPresent(onlineItem.ItemId);

                VoiceSynthesizer.Request(item);

                var missingFiles = MediaFileResolver.GetMissingFileNames(
                    onlineItem.MediaFileNames, TachieFileResolver.GetBaseDirectory(item));
                if (missingFiles.Count > 0 && activeViewModel != null)
                {
                    TransferWaiter.WhenFilesReady(activeViewModel, missingFiles, () =>
                        JsonConvert.PopulateObject(
                            LocalMediaGuard.StripVoiceCache(LocalMediaGuard.Apply(
                                MediaFileResolver.ResolveJsonFileReferences(onlineItem.ItemJson, itemType, onlineItem.MediaFileNames),
                                item)),
                            item,
                            ItemSerializerOptions.Default),
                        TransferWaiter.DefaultTimeout);
                }
            }
            catch
            {
                RenderReadiness.MarkMissing(onlineItem.ItemId, TimelineIndexOf(timeline));
            }
        }

        internal void HandleSyncRequestEvent(SyncRequestEvent evt)
        {
            var isAddressedToMe = evt.SourceUserId == LocalUserId
                || (evt.SourceUserId == null && localUserRole == UserRole.Host);
            if (!isAddressedToMe) return;

            SendSyncScenes(evt.ExecutorId.ToString(), evt.IsManual, transferFiles: !webParticipants.ContainsKey(evt.ExecutorId), knownDigests: evt.KnownDigests);
        }

        private static string ItemDigest(OnlineItem item)
        {
            var text = $"{item.ItemTypeName}|{item.Frame}|{item.Layer}|{string.Join('/', item.MediaFileNames ?? [])}|{item.ItemJson}";
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
        }

        private void SendSyncScenes(string? targetId, bool isManual, long baseSeq = 0, bool transferFiles = true, IReadOnlyDictionary<Guid, string>? knownDigests = null)
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
                    var (rawJson, files) = MediaFileResolver.SerializeForSync(item, itemType);
                    var onlineItem = new OnlineItem
                    {
                        ItemId = ItemIdManager.GetOrCreateId(item),
                        ItemTypeName = ItemTypeResolver.GetTypeName(itemType),
                        ItemJson = LocalMediaGuard.StripVoiceCache(rawJson),
                        MediaFileNames = files.Count > 0 ? [.. files.Where(file => file.ShouldTransfer).Select(file => file.Name)] : null,
                        Frame = item.Frame,
                        Layer = item.Layer
                    };
                    if (knownDigests != null)
                    {
                        onlineItem.Digest = ItemDigest(onlineItem);
                        if (knownDigests.TryGetValue(onlineItem.ItemId, out var known) && known == onlineItem.Digest)
                        {
                            onlineItem.ItemJson = string.Empty;
                            onlineItem.MediaFileNames = null;
                            onlineItem.Unchanged = true;
                        }
                    }
                    onlineTimeline.Items.Add(onlineItem);

                    var characterName = CharacterResolver.GetCharacter(item)?.Name;
                    filesToTransfer.AddRange(files.Where(file => file.ShouldTransfer).Select(file => (file, characterName)));
                }
                onlineScenes.Timelines.Add(onlineTimeline);
            }

            var syncId = Guid.NewGuid();

            _ = sessionClient.SendAsync(targetId, new SyncScenesBeginEvent(syncId, isManual, baseSeq)
            {
                DateTime = DateTime.UtcNow,
                ExecutorId = LocalUserId
            });

            _ = SendSyncChunksAsync(targetId, syncId, onlineScenes, isManual);

            if (!transferFiles) return;

            fileTransferManager.ForgetAnnouncedFiles();

            foreach (var (file, characterName) in filesToTransfer.DistinctBy(entry => entry.File.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                if (fileTransferManager.IsSendDenied(file.FullPath)) continue;

                fileTransferManager.MarkSendAllowed(file.FullPath);
                _ = fileTransferManager.TransferAsync(file, characterName, sessionClient, LocalUserId);
            }
        }

        private Task SendSyncChunksAsync(string? targetId, Guid syncId, OnlineScenes onlineScenes, bool isManual) =>
            Task.Run(async () =>
            {
                try
                {
                    var (overFileChannel, overControlChannel) = SplitSyncRecipients(targetId);

                    foreach (var chunk in SyncScenesTransfer.Split(syncId, onlineScenes, isManual))
                    {
                        if (!sessionClient.IsConnected) return;

                        var stamped = chunk with
                        {
                            DateTime = DateTime.UtcNow,
                            ExecutorId = LocalUserId
                        };

                        if (overFileChannel.Count > 0) await sessionClient.SendFileDataToManyAsync(overFileChannel, stamped);

                        if (overControlChannel == null) await sessionClient.SendAsync(targetId, stamped);
                        else if (overControlChannel.Count > 0) await sessionClient.SendToManyAsync(overControlChannel, stamped);
                    }
                }
                catch
                {
                }
            });

        private (List<string> OverFileChannel, List<string>? OverControlChannel) SplitSyncRecipients(string? targetId)
        {
            if (IsKeptRoom || targetId == "server" || !sessionClient.HasFileChannel) return ([], null);

            var recipients = targetId != null && Guid.TryParse(targetId, out var target)
                ? [target]
                : Participants.Where(p => p.UserId != LocalUserId).Select(p => p.UserId).ToList();

            var overFile = new List<string>();
            var overControl = new List<string>();
            foreach (var recipient in recipients)
            {
                var hasChannel = peerFileChannels.TryGetValue(recipient, out var channel) && channel;
                (hasChannel ? overFile : overControl).Add(recipient.ToString());
            }

            return overFile.Count == 0 ? ([], null) : (overFile, overControl);
        }

        private bool IsTrustedSyncSource(Guid executorId) =>
            IsFromHost(executorId) || (IsKeptRoom && executorId == SessionClient.ServerUserId);

        internal void HandleSyncScenesBegin(SyncScenesBeginEvent evt)
        {
            if (!evt.IsManual && !IsTrustedSyncSource(evt.ExecutorId)) return;

            syncScenesTransfer.BeginHolding(evt.SyncId);

            if (syncScenesTransfer.TryTakeCompleted(evt.SyncId, out var completed))
                ApplyAndReleaseSync(completed.Scenes, completed.ExecutorId, completed.IsManual);
        }

        internal void HandleSyncScenesChunk(SyncScenesChunkEvent evt)
        {
            if (!evt.IsManual && !IsTrustedSyncSource(evt.ExecutorId)) return;

            var scenes = syncScenesTransfer.Receive(evt);
            if (scenes == null) return;

            if (!syncScenesTransfer.IsHoldingFor(evt.SyncId))
            {
                syncScenesTransfer.KeepUntilBegin(evt.SyncId, scenes, evt.ExecutorId, evt.IsManual);
                return;
            }

            ApplyAndReleaseSync(scenes, evt.ExecutorId, evt.IsManual);
        }

        private void ApplyAndReleaseSync(OnlineScenes scenes, Guid executorId, bool isManual)
        {
            try
            {
                RenderReadiness.Clear();
                ApplySyncScenes(scenes, executorId, isManual);
            }
            finally
            {
                ReleaseHeldEvents();
            }
        }

        private void ReleaseHeldEvents()
        {
            var heldEvents = syncScenesTransfer.TakeHeld();

            for (var index = 0; index < heldEvents.Count; index++)
                DispatchEvent(heldEvents[index], suppressRedraw: index < heldEvents.Count - 1);
        }

        private void DispatchEvent(EditEvent editEvent, bool suppressRedraw)
        {
            if (editEvent is SequenceMarkerEvent marker)
            {
                if (marker.Seq > lastAppliedSeq) lastAppliedSeq = marker.Seq;
                return;
            }

            if (activeViewModel == null) return;

            var previous = isApplyingRemoteEvent;
            isApplyingRemoteEvent = true;
            using var undoScope = suppressRedraw
                ? UndoRecordSuppressor.SuppressSilently(undoRedoManager)
                : UndoRecordSuppressor.Suppress(undoRedoManager);
            try
            {
                eventDispatcher.Dispatch(editEvent, activeViewModel);
            }
            catch
            {
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

        private DateTime heardBeforeDrop = DateTime.Now;
    }
}
