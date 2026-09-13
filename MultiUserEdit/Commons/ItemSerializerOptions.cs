using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Reflection;

namespace MultiUserEdit.Commons
{
    internal static class ItemSerializerOptions
    {
        public static JsonSerializerSettings Default { get; } = CreateOptions(nullPath: false);
        public static JsonSerializerSettings NullPath { get; } = CreateOptions(nullPath: true);

        public static JsonSerializerSettings Character { get; } = CreateCharacterOptions(nullPath: false);
        public static JsonSerializerSettings CharacterNullPath { get; } = CreateCharacterOptions(nullPath: true);

        private static JsonSerializerSettings CreateCharacterOptions(bool nullPath)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Formatting = Formatting.None,
                Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
                Error = (_, args) => args.ErrorContext.Handled = true
            };

            if (nullPath)
            {
                settings.ContractResolver = new NullFilePathContractResolver();
            }

            return settings;
        }

        private static JsonSerializerSettings CreateOptions(bool nullPath)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                Formatting = Formatting.None,
                ContractResolver = nullPath ? new NullFilePathContractResolver() : new CollectionReplaceContractResolver()
            };

            return settings;
        }

        private class CollectionReplaceContractResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);

                var propertyType = property.PropertyType;
                if (propertyType != null
                    && propertyType != typeof(string)
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
                {
                    property.ObjectCreationHandling = ObjectCreationHandling.Replace;
                }

                return property;
            }
        }

        private class NullFilePathContractResolver : CollectionReplaceContractResolver
        {
            private static readonly HashSet<string> PathPropertyNames = new(MediaFileResolver.PropertyNames, StringComparer.OrdinalIgnoreCase);

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);

                if (property.PropertyType == typeof(string) &&
                    PathPropertyNames.Contains(property.PropertyName ?? string.Empty))
                {
                    var originalProvider = property.ValueProvider;
                    property.ValueProvider = new FileNameValueProvider(originalProvider);

                    property.ShouldSerialize = instance =>
                        !string.IsNullOrWhiteSpace(originalProvider?.GetValue(instance) as string);
                }

                return property;
            }

            private class FileNameValueProvider(IValueProvider? originalProvider) : IValueProvider
            {
                public object? GetValue(object target)
                {
                    var val = (string)originalProvider?.GetValue(target)!;
                    try
                    {
                        var portableName = MediaFileResolver.ToPortableName(val);
                        return string.IsNullOrEmpty(portableName) ? val : portableName;
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
