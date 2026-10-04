using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace MultiUserEdit.Commons
{
    internal static class ItemJsonSanitizer
    {
        private const long MaxPlausibleEnumValue = 10000;

        private static readonly string[] EnumPropertyNames =
        [
            "Type", "AnimationType", "EasingType", "BlendMode", "DrawingMode", "Direction"
        ];

        public static string RemoveUnknownEnumValues(string itemJson)
        {
            try
            {
                if (JToken.Parse(itemJson) is not JContainer root) return itemJson;

                var removed = root.DescendantsAndSelf()
                    .OfType<JProperty>()
                    .Where(property => property.Value.Type == JTokenType.Integer
                        && EnumPropertyNames.Contains(property.Name, StringComparer.Ordinal)
                        && Math.Abs((long)property.Value) >= MaxPlausibleEnumValue)
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
