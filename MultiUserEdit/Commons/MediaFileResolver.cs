using System.IO;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class MediaFileResolver
    {
        // IFileItem を実装しない旧来のアイテム向けフォールバック（VideoItem/AudioItem/ImageItem/TachieItem等は
        // すべてIFileItemを実装しているため、通常はこちらのリフレクション経路には入らない）
        internal static readonly string[] PropertyNames = ["FilePath", "PsdPath", "ImagePath", "TachiEPath", "PsdFilePath", "ImageFilePath", "SourcePath", "File", "Path"];

        // 1x1 透明PNGバイト列（WICデコーダー・Direct2D等のコンポーネント未検出エラー0x88982F50を回避）
        private static readonly byte[] TransparentPngBytes =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
            0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        ];

        public static string ResolveLocalTempPath(string originalFilePath)
        {
            if (string.IsNullOrEmpty(originalFilePath)) return string.Empty;
            var fileName = Path.GetFileName(originalFilePath);
            var saveDir = FileTransferManager.GetSaveDirectory();
            return Path.Combine(saveDir, fileName);
        }

        // VideoItem/AudioItemはMedia Foundationで実データをデコードするため、透明PNGのプレースホルダーを
        // 渡すと「指定されたURLのバイトストリームタイプはサポートされていません」等の例外を投げる
        // （画像系はWICが内容ベースで判定するためプレースホルダーでも問題なく動くが、動画・音声はコンテナ
        // 形式が一致しないと即座に拒否され、しかもYMM4側で捕捉されず未処理例外としてアプリが落ちる）。
        // そのため動画・音声は転送完了までプレースホルダーを使わず、参照そのものを空にしておく。
        public static bool RequiresRealMediaContainer(IItem item) => item is VideoItem or AudioItem;

        public static void ClearRealMediaFilePath(IItem item)
        {
            if (item is VideoItem videoItem) videoItem.FilePath = null;
            else if (item is AudioItem audioItem) audioItem.FilePath = null;
        }

        public static void EnsurePlaceholderFile(string targetSavePath)
        {
            if (string.IsNullOrEmpty(targetSavePath)) return;
            if (File.Exists(targetSavePath)) return;

            try
            {
                var dir = Path.GetDirectoryName(targetSavePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // すべての画像・素材デコーダーが正常に読み込めるダミーPNGプレースホルダーを事前生成
                File.WriteAllBytes(targetSavePath, TransparentPngBytes);
            }
            catch { }
        }

        // アイテムが参照するファイルパスをすべて列挙する（ローカルに存在するかは問わない）。
        // VideoItem/AudioItem/ImageItem/TachieItem等はすべてSDKのIFileItemを実装しているため、
        // 立ち絵の表情差分（ネストしたCharacterパラメーター内のファイル）もトップレベルのプロパティ名に
        // 依存せず正しく検出できる。
        public static IReadOnlyList<string> GetReferencedPaths(IItem item)
        {
            if (item is IFileItem fileItem)
            {
                return fileItem.GetFiles()
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var legacyPath = GetFilePath(item);
            return string.IsNullOrEmpty(legacyPath) ? [] : [legacyPath];
        }

        // アイテムが参照するファイルのうち、ローカルに実在するもの（＝送信可能なもの）だけを列挙する。
        public static IReadOnlyList<string> GetFilePaths(IItem item)
        {
            return GetReferencedPaths(item).Where(File.Exists).ToList();
        }

        // 更新イベント受信時、JsonConvert.PopulateObjectで「表示中の生きたアイテム」に直接反映する前に、
        // JSON文字列の時点でファイル名を解決しておく。PopulateObject後にファイルパスを直す方式だと、
        // その一瞬だけ壊れた値がWPFにバインドされたプロパティへ反映されてしまい、ちょうどそのタイミングで
        // 再描画が走ると存在しないファイルを読みに行ってクラッシュする。
        //
        // mediaFileNamesは送信側がSerializeForSync時点で実際にファイル名へ差し替えた対象そのもの
        // （ItemAddedEvent.MediaFileNames同様、送信側から渡される正の情報）。受信側アイテムの「現在の
        // FilePath」から逆算する方式だと、動画・音声がまだ初回転送中でFilePathがnullのままの場合に
        // 対象を見失い、素のファイル名がそのままPopulateObjectで生きたVideoItem等に入ってしまい、
        // Media Foundationが未処理例外を投げていた。
        public static string ResolveJsonFileReferences(string itemJson, IItem item, IReadOnlyList<string>? mediaFileNames)
        {
            if (mediaFileNames == null || mediaFileNames.Count == 0) return itemJson;

            var requiresRealContainer = RequiresRealMediaContainer(item);

            foreach (var fileName in mediaFileNames)
            {
                if (string.IsNullOrEmpty(fileName)) continue;

                var resolvedPath = ResolveLocalTempPath(fileName);

                if (File.Exists(resolvedPath))
                {
                    // 初回転送が完了済みで実データが既にローカルにある場合はそのまま差し替える
                    itemJson = itemJson.Replace($"\"{fileName}\"", $"\"{EscapeForJson(resolvedPath)}\"");
                }
                else if (requiresRealContainer)
                {
                    // 動画・音声はまだ実データが届いていない。プレースホルダーを渡すとMedia Foundationが
                    // 例外を投げるため触らずnullにしておく（初回転送完了時に正しいパスが設定される）
                    itemJson = itemJson.Replace($"\"{fileName}\"", "null");
                }
                else
                {
                    // 画像系はプレースホルダーで問題なく動くため即時生成して差し替える
                    EnsurePlaceholderFile(resolvedPath);
                    itemJson = itemJson.Replace($"\"{fileName}\"", $"\"{EscapeForJson(resolvedPath)}\"");
                }
            }

            return itemJson;
        }

        // アイテムが保持するファイル参照を置き換える。IFileItemを実装していれば、ネストしたパラメーター内の
        // 参照も含めてSDK側のロジックで正しく置換される。
        public static bool ReplaceFilePath(IItem item, string from, string to)
        {
            if (item is IFileItem fileItem)
            {
                fileItem.ReplaceFile(from, to);
                return true;
            }

            return SetFilePath(item, to);
        }

        public static string? GetFilePath(IItem item)
        {
            if (item is VideoItem videoItem) return videoItem.FilePath;
            if (item is AudioItem audioItem) return audioItem.FilePath;
            if (item is ImageItem imageItem) return imageItem.FilePath;

            var type = item.GetType();
            foreach (var propName in PropertyNames)
            {
                var prop = type.GetProperty(propName);
                if (prop != null && prop.GetValue(item) is string val && !string.IsNullOrEmpty(val))
                    return val;
            }
            return null;
        }

        public static string? GetFileName(IItem item)
        {
            var path = GetFilePath(item);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        }

        public static string SerializeWithNullPath(IItem item, Type? itemType = null)
        {
            return itemType != null
                ? Newtonsoft.Json.JsonConvert.SerializeObject(item, itemType, ItemSerializerOptions.NullPath)
                : Newtonsoft.Json.JsonConvert.SerializeObject(item, ItemSerializerOptions.NullPath);
        }

        // ローカル環境のフルパス（ユーザー名等を含む）をそのまま相手に送らないようにするための処理。
        // 戻り値の files は実際にローカルへ存在し転送が必要なファイルの絶対パス一覧（filter適用後）。
        //
        // VideoItem/AudioItem/ImageItem等のトップレベルのプロパティは、既存の名前ベースの置換
        // （ItemSerializerOptions.NullPath、非破壊）だけで十分にファイル名のみへ変換できる。
        // 立ち絵の表情差分のようにネストしたプロパティ名が予測できないケースだけ、シリアライズ直前の
        // 一瞬だけライブオブジェクトの参照をファイル名へ差し替えて再シリアライズし、直後に元へ戻す
        // （FilePath等はWPFにバインドされているため、不要にこれを行うとUIが壊れたパスを読みに行ってしまう）。
        public static (string itemJson, IReadOnlyList<string> files) SerializeForSync(IItem item, Type? itemType = null, Func<string, bool>? filter = null)
        {
            var files = GetFilePaths(item).Where(fp => filter == null || filter(fp)).ToList();

            if (files.Count == 0)
                return (SerializeWithNullPath(item, itemType), files);

            var itemJson = SerializeWithNullPath(item, itemType);

            var unresolved = files.Where(fp => itemJson.Contains(EscapeForJson(fp))).ToList();
            if (unresolved.Count > 0)
            {
                foreach (var fp in unresolved)
                    ReplaceFilePath(item, fp, Path.GetFileName(fp));

                itemJson = SerializeWithNullPath(item, itemType);

                foreach (var fp in unresolved)
                    ReplaceFilePath(item, Path.GetFileName(fp), fp);
            }

            return (itemJson, files);
        }

        private static string EscapeForJson(string path) => path.Replace("\\", "\\\\");

        public static bool SetFilePath(IItem item, string newFilePath)
        {
            if (item is VideoItem videoItem)
            {
                videoItem.FilePath = newFilePath;
                return true;
            }
            if (item is AudioItem audioItem)
            {
                audioItem.FilePath = newFilePath;
                return true;
            }
            if (item is ImageItem imageItem)
            {
                imageItem.FilePath = newFilePath;
                return true;
            }

            var type = item.GetType();
            foreach (var propName in PropertyNames)
            {
                var prop = type.GetProperty(propName);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(item, newFilePath);
                    return true;
                }
            }
            return false;
        }
    }
}
