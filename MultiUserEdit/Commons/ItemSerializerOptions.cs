using Newtonsoft.Json;

namespace MultiUserEdit.Commons
{
    internal static class ItemSerializerOptions
    {
        public static JsonSerializerSettings Default { get; } = CreateOptions();

        private static JsonSerializerSettings CreateOptions()
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                Formatting = Formatting.None
            };
            return settings;
        }
    }
}
