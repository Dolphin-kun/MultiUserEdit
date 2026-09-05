using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;
using System.IO;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class ItemAddedEventHandler : IClientEventHandler<ItemAddedEvent>
    {
        public void Handle(ItemAddedEvent editEvent, MultiUserEditViewModel viewModel)
        {
            var timeline = viewModel.Scenes?.Timelines.ElementAtOrDefault(editEvent.TimelineIndex);
            if (timeline == null) return;

            // 同じIDのアイテムが既にあるなら追加しない。
            // 何らかの理由で追加イベントが二重に届いても、ゴーストアイテムが生まれないようにする。
            if (timeline.Items.Any(existing => ItemIdManager.GetOrCreateId(existing) == editEvent.ItemId)) return;

            try
            {
                var itemType = Type.GetType(editEvent.ItemTypeName);
                if (itemType == null) return;

                if (Newtonsoft.Json.JsonConvert.DeserializeObject(editEvent.ItemJson, itemType, ItemSerializerOptions.Default) is not IItem item)
                    return;

                CharacterResolver.TryResolveCharacter(item);
                SceneResolver.ResolveSceneId(item, viewModel.Scenes);

                if (editEvent.ExecutorId == viewModel.LocalUserId)
                {
                    ItemIdManager.RegisterId(item, editEvent.ItemId);
                    timeline.TryAddItems([item], editEvent.Frame, editEvent.Layer, false);
                    return;
                }

                // 送信側でファイル名のみに差し替えられた参照を、受信側のローカル保存先パスへ差し替える。
                // 画像系はプレースホルダーを即時生成して差し替えるが、動画・音声はMedia Foundationが
                // プレースホルダーを拒否して未処理例外になるため、転送完了まで参照を空のままにする。
                if (editEvent.MediaFileNames is { Count: > 0 })
                {
                    var requiresRealContainer = MediaFileResolver.RequiresRealMediaContainer(item);

                    foreach (var mediaFileName in editEvent.MediaFileNames)
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

                        // 動画・音声以外は既にReplaceFilePathで正しい最終パスになっている
                        // （転送完了時は中身が差し替わるだけでパス自体は変わらない）ため、
                        // 動画・音声（転送完了まで参照をnullにしている）の場合だけ完了を待つ
                        if (requiresRealContainer)
                        {
                            var fileName = Path.GetFileName(targetSavePath);

                            void onCompleted(string transferId, string savedPath)
                            {
                                if (Path.GetFileName(savedPath).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                                {
                                    viewModel.FileTransferCompleted -= onCompleted;

                                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                                    {
                                        viewModel.ExecuteRemoteAction(() => MediaFileResolver.SetFilePath(item, savedPath));
                                    });
                                }
                            }

                            viewModel.FileTransferCompleted += onCompleted;
                        }
                    }
                }

                ItemIdManager.RegisterId(item, editEvent.ItemId);
                timeline.TryAddItems([item], editEvent.Frame, editEvent.Layer, false);
            }
            catch { }
        }
    }
}
