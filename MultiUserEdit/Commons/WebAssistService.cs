using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Windows;
using MultiUserEdit.Commons.Events;
using Newtonsoft.Json;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;

namespace MultiUserEdit.Commons
{
    internal sealed class WebAssistService(
        Func<Scenes?> getScenes,
        Func<Guid, bool> canAddItems,
        Func<Guid, EditEvent, Task> send,
        Func<string, string?> resolveMedia,
        Func<string, Task<bool>> confirmMedia,
        Action<Guid, string, string> sendMedia,
        Func<string, bool> isReceiving)
    {
        private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan NotReceivingGrace = TimeSpan.FromSeconds(3);

        private const int MaxCachedSchemas = 256;
        private readonly ConcurrentDictionary<string, string> schemaCache = new(StringComparer.Ordinal);

        public void Handle(EditEvent request)
        {
            switch (request)
            {
                case ItemSchemaRequestEvent schema:
                    _ = Task.Run(() => SendSchemaAsync(schema));
                    break;
                case CreateItemRequestEvent create:
                    Application.Current?.Dispatcher.InvokeAsync(() => CreateItemAsync(create));
                    break;
                case MediaRequestEvent media:
                    Application.Current?.Dispatcher.InvokeAsync(() => ServeMediaAsync(media));
                    break;
                case CatalogRequestEvent catalog:
                    Application.Current?.Dispatcher.InvokeAsync(() => SendCatalog(catalog));
                    break;
                case ItemVisualsRequestEvent visuals:
                    Application.Current?.Dispatcher.InvokeAsync(() => SendVisuals(visuals));
                    break;
            }
        }

        private async Task SendSchemaAsync(ItemSchemaRequestEvent request)
        {
            try
            {
                var schema = schemaCache.TryGetValue(request.TypeName, out var cached) ? cached : BuildSchema(request.TypeName);
                if (cached == null && schema != "[]" && schemaCache.Count < MaxCachedSchemas) schemaCache.TryAdd(request.TypeName, schema);
                await send(request.ExecutorId, new ItemSchemaEvent(request.RequestId, request.TypeName, schema) { DateTime = DateTime.UtcNow });
            }
            catch
            {
            }
        }

        private void SendVisuals(ItemVisualsRequestEvent request)
        {
            var timeline = getScenes()?.Timelines.ElementAtOrDefault(request.TimelineIndex);
            if (timeline == null) return;

            var wanted = request.ItemIds is { Count: > 0 } ids ? new HashSet<Guid>(ids) : null;
            var entries = new List<object>();
            foreach (var item in timeline.Items)
            {
                var id = ItemIdManager.GetOrCreateId(item);
                if (wanted != null && !wanted.Contains(id)) continue;

                var label = SafeText(() => item is BaseItem baseItem ? baseItem.Label : null) ?? string.Empty;
                var color = item is BaseItem colored ? colored.ItemColor : default;
                entries.Add(new { i = id, l = label, c = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}" });
            }

            _ = send(request.ExecutorId, new ItemVisualsEvent(request.RequestId, request.TimelineIndex, JsonConvert.SerializeObject(entries)) { DateTime = DateTime.UtcNow });
        }

        private async Task ServeMediaAsync(MediaRequestEvent request)
        {
            void Reply(bool success, string message) =>
                _ = send(request.ExecutorId, new MediaResultEvent(request.RequestId, request.FileName, success, message) { DateTime = DateTime.UtcNow });

            try
            {
                var path = resolveMedia(request.FileName);
                if (path == null)
                {
                    Reply(false, "missing");
                    return;
                }

                if (!await confirmMedia(path))
                {
                    Reply(false, "denied");
                    return;
                }

                if (!request.AudioOnly || AudioProxy.CanSendAsIs(path))
                {
                    Reply(true, string.Empty);
                    sendMedia(request.ExecutorId, path, request.FileName);
                    return;
                }

                var cached = AudioProxy.CachedPath(path);
                Reply(true, cached != null ? "audio" : "converting");
                var proxy = cached ?? await AudioProxy.GetOrCreateAsync(path);
                if (proxy == null)
                {
                    Reply(false, "noaudio");
                    return;
                }
                if (cached == null) Reply(true, "audio");
                sendMedia(request.ExecutorId, proxy, request.FileName);
            }
            catch (Exception ex)
            {
                Reply(false, ex.GetType().Name);
            }
        }

        private void SendCatalog(CatalogRequestEvent request)
        {
            string json;
            try
            {
                json = request.Kind switch
                {
                    "animation" => JsonConvert.SerializeObject(AnimationCatalog()),
                    "videoEffects" => JsonConvert.SerializeObject(EffectCatalog(PluginLoader.VideoEffects, type => type.GetCustomAttribute<VideoEffectAttribute>() is { } a ? (a.GetName(), a.GetCategories(), a.GetKeywords()) : null)),
                    "audioEffects" => JsonConvert.SerializeObject(EffectCatalog(PluginLoader.AudioEffects, type => type.GetCustomAttribute<AudioEffectAttribute>() is { } a ? (a.GetName(), a.GetCategories(), a.GetKeywords()) : null)),
                    "template" => EffectTemplate(request.Argument),
                    "characters" => JsonConvert.SerializeObject((CharacterSettingsAccessor.GetCharacters() ?? []).Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => new { name = c.Name, color = $"#{c.Color.R:X2}{c.Color.G:X2}{c.Color.B:X2}" })),
                    _ => "null"
                };
            }
            catch
            {
                json = "null";
            }
            _ = send(request.ExecutorId, new CatalogEvent(request.RequestId, request.Kind, json) { DateTime = DateTime.UtcNow });
        }

        private static List<object> AnimationCatalog()
        {
            var shortNames = new AnimationTypeToCharConverter();
            var list = new List<object>();
            foreach (AnimationType type in Enum.GetValues<AnimationType>())
            {
                var value = (int)type;
                var group = value >= 100000 ? "bezier" : value > 10000 ? "easing" : "standard";
                list.Add(new
                {
                    value,
                    name = SafeText(() => AnimationTypeToToolTipConverter.ConvertText(type, ignoreModes: false)) ?? type.ToString(),
                    shortName = SafeText(() => shortNames.Convert(type, typeof(string), null!, System.Globalization.CultureInfo.CurrentUICulture)?.ToString()) ?? "",
                    group,
                    keyFrames = type.IsKeyFrameSupported(),
                    span = type is AnimationType.移動量指定 or AnimationType.ランダム移動 or AnimationType.反復移動,
                });
            }
            return list;
        }

        private static List<object> EffectCatalog(IEnumerable<Type> types, Func<Type, (string Name, string[] Categories, string[] Keywords)?> describe)
        {
            var list = new List<object>();
            foreach (var type in types)
            {
                var info = SafeValue(() => describe(type));
                if (info == null || string.IsNullOrEmpty(info.Value.Name)) continue;
                list.Add(new { type = ItemTypeResolver.GetTypeName(type), name = info.Value.Name, categories = info.Value.Categories, keywords = info.Value.Keywords });
            }
            return list;
        }

        private static string EffectTemplate(string? typeName)
        {
            var type = ItemTypeResolver.Resolve(typeName);
            if (type == null || type.IsAbstract) return "null";
            if (!typeof(IVideoEffect).IsAssignableFrom(type) && !typeof(IAudioEffect).IsAssignableFrom(type)) return "null";
            var effect = Activator.CreateInstance(type);
            return effect == null ? "null" : JsonConvert.SerializeObject(effect, ItemSerializerOptions.Default);
        }

        private static T? SafeValue<T>(Func<T?> read) where T : struct
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        private async Task<IItem?> CreateFromFileAsync(CreateItemRequestEvent request, Timeline timeline)
        {
            if (string.IsNullOrWhiteSpace(request.FileName)) return null;

            var started = DateTime.UtcNow;
            var path = resolveMedia(request.FileName);
            while (path == null && DateTime.UtcNow - started < ReceiveWait)
            {
                if (!isReceiving(request.FileName) && DateTime.UtcNow - started > NotReceivingGrace) break;
                await Task.Delay(250);
                path = resolveMedia(request.FileName);
            }
            if (path == null) return null;

            var type = FileSettings.Default.FileExtensions.GetFileType(path);
            if (type.HasFlag(FileType.動画) && ItemSettings.Default.DefaultItems.VideoItem?.GetClone() is VideoItem video)
            {
                video.FilePath = path;
                FitLengthToContent(video, timeline.VideoInfo.FPS);
                return video;
            }
            if (type.HasFlag(FileType.音声) && ItemSettings.Default.DefaultItems.AudioItem?.GetClone() is AudioItem audio)
            {
                audio.FilePath = path;
                FitLengthToContent(audio, timeline.VideoInfo.FPS);
                return audio;
            }
            if (type.HasFlag(FileType.画像) && ItemSettings.Default.DefaultItems.ImageItem?.GetClone() is ImageItem image)
            {
                image.FilePath = path;
                return image;
            }
            return null;
        }

        private static void FitLengthToContent(IItem item, int fps)
        {
            item.ContentOffset = TimeSpan.Zero;
            var rate = item.PlaybackRate2.Values.FirstOrDefault()?.Value ?? 100.0;
            if (item.ContentLength > TimeSpan.Zero && rate > 0 && fps > 0)
                item.Length = Math.Max(1, (int)Math.Ceiling(item.ContentLength.TotalSeconds * 100.0 / rate * fps));
        }

        private async Task CreateItemAsync(CreateItemRequestEvent request)
        {
            void Reply(bool success, string message, Guid itemId = default) =>
                _ = send(request.ExecutorId, new CreateItemResultEvent(request.RequestId, success, itemId, message) { DateTime = DateTime.UtcNow });

            try
            {
                if (!canAddItems(request.ExecutorId))
                {
                    Reply(false, "denied");
                    return;
                }

                var scenes = getScenes();
                var timeline = scenes?.Timelines.FirstOrDefault(t => t.ID == request.SceneId)
                    ?? scenes?.Timelines.ElementAtOrDefault(request.TimelineIndex);
                if (timeline == null)
                {
                    Reply(false, "scene");
                    return;
                }

                IItem? item = request.Kind switch
                {
                    "Text" => ItemSettings.Default.DefaultItems.TextItem?.GetClone(),
                    "Shape" => ItemSettings.Default.DefaultItems.ShapeItem?.GetClone(),
                    "File" => await CreateFromFileAsync(request, timeline),
                    _ => null
                };
                if (item == null)
                {
                    Reply(false, request.Kind == "File" ? "file" : "kind");
                    return;
                }

                if (timeline.TryAddItems([item], Math.Max(0, request.Frame), Math.Max(0, request.Layer), false))
                {
                    Reply(true, string.Empty, ItemIdManager.GetOrCreateId(item));
                    return;
                }
                Reply(false, "space");
            }
            catch (Exception ex)
            {
                Reply(false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static string BuildSchema(string typeName)
        {
            var type = ItemTypeResolver.Resolve(typeName);
            if (type == null || type.IsAbstract) return "[]";
            if (!typeof(IItem).IsAssignableFrom(type) && !typeof(IVideoEffect).IsAssignableFrom(type) && !typeof(IAudioEffect).IsAssignableFrom(type)) return "[]";

            var properties = new List<object>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var display = property.GetCustomAttribute<DisplayAttribute>();
                if (display == null || display.GetAutoGenerateField() == false) continue;

                var label = SafeText(display.GetName);
                if (property.GetCustomAttributes().Any(a => a is VideoEffectSelectorAttribute or AudioEffectSelectorAttribute))
                {
                    properties.Add(new Dictionary<string, object?>
                    {
                        ["name"] = property.Name,
                        ["label"] = label ?? string.Empty,
                        ["group"] = SafeText(display.GetGroupName) ?? string.Empty,
                        ["order"] = display.GetOrder() ?? 0,
                        ["editor"] = property.GetCustomAttributes().Any(a => a is AudioEffectSelectorAttribute) ? "audioEffects" : "videoEffects",
                    });
                    continue;
                }
                if (string.IsNullOrEmpty(label))
                {
                    if (display.GetAutoGenerateField() == true && !property.PropertyType.IsValueType && property.PropertyType != typeof(string))
                    {
                        properties.Add(new Dictionary<string, object?>
                        {
                            ["name"] = property.Name,
                            ["label"] = string.Empty,
                            ["group"] = SafeText(display.GetGroupName) ?? string.Empty,
                            ["order"] = display.GetOrder() ?? 0,
                            ["editor"] = "nested",
                        });
                    }
                    continue;
                }

                var editor = property.GetCustomAttributes().FirstOrDefault(a => a is PropertyEditorAttribute2);
                var entry = new Dictionary<string, object?>
                {
                    ["name"] = property.Name,
                    ["label"] = label,
                    ["group"] = SafeText(display.GetGroupName) ?? string.Empty,
                    ["order"] = display.GetOrder() ?? 0,
                    ["description"] = SafeText(display.GetDescription),
                    ["editor"] = DescribeEditor(property, editor),
                    ["size"] = (editor as PropertyEditorAttribute2)?.PropertyEditorSize switch
                    {
                        PropertyEditorSize.Half => "half",
                        PropertyEditorSize.FullWidth => "full",
                        PropertyEditorSize.Normal => "normal",
                        _ => property.PropertyType == typeof(bool) ? "half" : "normal",
                    },
                };

                switch (editor)
                {
                    case AnimationSliderAttribute slider:
                        entry["format"] = slider.StringFormat;
                        entry["unit"] = slider.UnitText;
                        entry["min"] = slider.DefaultMin;
                        entry["max"] = slider.DefaultMax;
                        break;
                    case TextBoxSliderAttribute slider:
                        entry["format"] = slider.StringFormat;
                        entry["unit"] = slider.UnitText;
                        entry["min"] = slider.DefaultMin;
                        entry["max"] = slider.DefaultMax;
                        break;
                    case TextEditorAttribute textEditor:
                        entry["multiline"] = textEditor.AcceptsReturn;
                        break;
                }

                if (property.PropertyType.IsEnum)
                {
                    entry["options"] = Enum.GetValues(property.PropertyType).Cast<object>()
                        .Select(value => new
                        {
                            value = Convert.ToInt64(value),
                            name = value.ToString(),
                            label = SafeText(() => property.PropertyType.GetField(value.ToString()!)?.GetCustomAttribute<DisplayAttribute>()?.GetName()) ?? value.ToString()
                        })
                        .ToList();
                }

                properties.Add(entry);
            }

            return JsonConvert.SerializeObject(properties);
        }

        private static string DescribeEditor(PropertyInfo property, Attribute? editor) => editor switch
        {
            _ when property.Name == nameof(BaseItem.ItemColor) => "itemcolor",
            AnimationSliderAttribute => "animation",
            TextBoxSliderAttribute => "number",
            ToggleSliderAttribute => "toggle",
            TextEditorAttribute => "text",
            ColorPickerAttribute => "color",
            EnumComboBoxAttribute => "enum",
            FontComboBoxAttribute => "font",
            FrameNumberEditorAttribute => "frame",
            VideoEffectSelectorAttribute => "videoEffects",
            AudioEffectSelectorAttribute => "audioEffects",
            null when property.PropertyType == typeof(string) => "text",
            null when property.PropertyType == typeof(bool) => "toggle",
            _ => "readonly",
        };

        private static string? SafeText(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }
}
