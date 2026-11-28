using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace MultiUserEdit.Commons
{
    internal static class ItemSerializerOptions
    {
        private static readonly SafeTypeBinder Binder = new();

        public static JsonSerializerSettings Default { get; } = CreateOptions(nullPath: false);
        public static JsonSerializerSettings NullPath { get; } = CreateOptions(nullPath: true);

        public static JsonSerializerSettings Character { get; } = CreateCharacterOptions(nullPath: false);
        public static JsonSerializerSettings CharacterNullPath { get; } = CreateCharacterOptions(nullPath: true);

        public static bool IsAllowedType(Type type) => SafeTypeBinder.IsAllowed(type);

        private static JsonSerializerSettings CreateCharacterOptions(bool nullPath)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                SerializationBinder = Binder,
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
                SerializationBinder = Binder,
                Formatting = Formatting.None,
                ContractResolver = nullPath ? new NullFilePathContractResolver() : new CollectionReplaceContractResolver()
            };

            return settings;
        }

        internal sealed class DisallowedTypeException(string typeName) : JsonSerializationException($"Type is not allowed: {typeName}");

        private sealed class SafeTypeBinder : ISerializationBinder
        {
            private static readonly DefaultSerializationBinder Inner = new();
            private static readonly ConcurrentDictionary<Type, bool> Verdicts = new();
            private static readonly HashSet<string> CollectionNamespaces = new(StringComparer.Ordinal)
            {
                "System.Collections.Generic",
                "System.Collections.Immutable",
                "System.Collections.ObjectModel",
            };
            private static readonly string PluginRoot =
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "user", "plugin")) + Path.DirectorySeparatorChar;

            public Type BindToType(string? assemblyName, string typeName)
            {
                var type = Inner.BindToType(assemblyName, typeName);
                if (!IsAllowed(type)) throw new DisallowedTypeException(string.IsNullOrEmpty(assemblyName) ? typeName : $"{typeName}, {assemblyName}");
                return type;
            }

            public void BindToName(Type serializedType, out string? assemblyName, out string? typeName) =>
                Inner.BindToName(serializedType, out assemblyName, out typeName);

            public static bool IsAllowed(Type type) => Verdicts.GetOrAdd(type, Check);

            private static bool Check(Type type)
            {
                if (type.IsArray) return type.GetElementType() is { } element && IsAllowed(element);
                if (type.IsGenericType && !type.IsGenericTypeDefinition)
                    return IsAllowed(type.GetGenericTypeDefinition()) && type.GetGenericArguments().All(IsAllowed);
                if (type.IsPointer || type.IsByRef || typeof(Delegate).IsAssignableFrom(type)) return false;
                if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return true;

                var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
                if (assemblyName.StartsWith("YukkuriMovieMaker", StringComparison.Ordinal)) return true;
                if (IsPluginAssembly(type.Assembly)) return true;

                var ns = type.Namespace ?? string.Empty;
                if (CollectionNamespaces.Contains(ns)) return true;
                return type.IsValueType && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal));
            }

            private static bool IsPluginAssembly(Assembly assembly)
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) return false;
                    return Path.GetFullPath(assembly.Location).StartsWith(PluginRoot, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
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
