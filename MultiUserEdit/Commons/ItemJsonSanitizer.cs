using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace MultiUserEdit.Commons
{
    internal static class ItemJsonSanitizer
    {
        private const string TypeProperty = "$type";

        private static readonly ConcurrentDictionary<string, Type?> typeCache = new(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<(Type Owner, string Property), Type?> enumCache = new();

        public static string RemoveUnknownEnumValues(string itemJson)
        {
            try
            {
                if (JToken.Parse(itemJson) is not JContainer root) return itemJson;

                var removed = root.DescendantsAndSelf()
                    .OfType<JProperty>()
                    .Where(IsUnknownEnumValue)
                    .ToList();

                if (removed.Count == 0) return itemJson;

                foreach (var property in removed)
                {
                    Debug.WriteLine($"[MultiUserEdit] dropped a value this environment does not know: {property.Name}={property.Value}");
                    property.Remove();
                }

                return root.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] sanitize failed: {ex.Message}");
                return itemJson;
            }
        }

        private static bool IsUnknownEnumValue(JProperty property)
        {
            if (property.Value.Type != JTokenType.Integer) return false;
            if (property.Parent is not JObject owner) return false;

            var enumType = ResolveEnumType(owner, property.Name);
            if (enumType == null) return false;

            try
            {
                var value = Convert.ChangeType((long)property.Value, Enum.GetUnderlyingType(enumType));
                return !Enum.IsDefined(enumType, value);
            }
            catch
            {
                return false;
            }
        }

        private static Type? ResolveEnumType(JObject owner, string propertyName)
        {
            if ((string?)owner[TypeProperty] is not { Length: > 0 } typeName) return null;

            var ownerType = typeCache.GetOrAdd(typeName, static name =>
            {
                try { return Type.GetType(name, throwOnError: false); }
                catch { return null; }
            });

            if (ownerType == null) return null;

            return enumCache.GetOrAdd((ownerType, propertyName), static key =>
            {
                try
                {
                    var property = key.Owner.GetProperty(key.Property, BindingFlags.Public | BindingFlags.Instance);
                    if (property?.PropertyType is not { IsEnum: true } type) return null;

                    return type.GetCustomAttribute<FlagsAttribute>() == null ? type : null;
                }
                catch
                {
                    return null;
                }
            });
        }

        public static string? GetMissingPluginName(Exception exception)
        {
            var message = exception.ToString();
            const string marker = "Error resolving type specified in JSON '";

            var start = message.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;

            start += marker.Length;
            var end = message.IndexOf('\'', start);
            if (end < 0) return null;

            var typeName = message[start..end];
            var parts = typeName.Split(',');

            return parts.Length >= 2 ? parts[1].Trim() : parts[0].Trim();
        }
    }
}
