using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.IO;
using System.Reflection;

namespace MultiUserEdit.Commons
{
    internal static class ItemSerializerOptions
    {
        public static JsonSerializerSettings Default { get; } = CreateOptions(nullPath: false);
        public static JsonSerializerSettings NullPath { get; } = CreateOptions(nullPath: true);

        private static JsonSerializerSettings CreateOptions(bool nullPath)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                Formatting = Formatting.None
            };

            if (nullPath)
            {
                settings.ContractResolver = new NullFilePathContractResolver();
            }

            return settings;
        }

        private class NullFilePathContractResolver : DefaultContractResolver
        {
            private static readonly HashSet<string> PathPropertyNames = new(MediaFileResolver.PropertyNames, StringComparer.OrdinalIgnoreCase);

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);

                if (PathPropertyNames.Contains(property.PropertyName ?? string.Empty))
                {
                    var originalProvider = property.ValueProvider;
                    property.ValueProvider = new FileNameValueProvider(originalProvider);

                    // ローカルにまだ値がない（動画・音声が初回転送中でnull等）場合はプロパティ自体を出力しない。
                    // 空文字列を送ってしまうと、更新イベントを受け取った他の参加者側で既に解決済みの
                    // 正しいパスまで空文字列で上書きしてしまう（PopulateObjectはJSONに存在するプロパティしか
                    // 触らないため、出力自体を省けば相手の既存の値はそのまま保たれる）。
                    property.ShouldSerialize = instance =>
                        !string.IsNullOrWhiteSpace(originalProvider?.GetValue(instance) as string);
                }

                return property;
            }

            private class FileNameValueProvider(IValueProvider? originalProvider) : IValueProvider
            {
                public object? GetValue(object target)
                {
                    // 値がない場合はShouldSerializeで出力自体がスキップされるためここには来ない
                    var val = (string)originalProvider?.GetValue(target)!;
                    try
                    {
                        var fileName = Path.GetFileName(val);
                        return string.IsNullOrEmpty(fileName) ? val : fileName;
                    }
                    catch
                    {
                        return val;
                    }
                }

                public void SetValue(object target, object? value)
                {
                    originalProvider?.SetValue(target, value);
                }
            }
        }
    }
}
