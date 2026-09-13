namespace MultiUserEdit.Commons
{
    internal static class ItemTypeResolver
    {
        public static string GetTypeName(Type type)
        {
            var fullName = type.FullName;
            var assemblyName = type.Assembly.GetName().Name;

            if (string.IsNullOrEmpty(fullName)) return type.AssemblyQualifiedName ?? string.Empty;
            if (string.IsNullOrEmpty(assemblyName)) return fullName;

            return $"{fullName}, {assemblyName}";
        }

        public static Type? Resolve(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;

            var type = Type.GetType(typeName, throwOnError: false);
            if (type != null) return type;

            var simpleName = RemoveAssemblyDetails(typeName);
            if (simpleName != typeName)
            {
                type = Type.GetType(simpleName, throwOnError: false);
                if (type != null) return type;
            }

            var fullName = simpleName.Split(',')[0].Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }

            return null;
        }

        private static string RemoveAssemblyDetails(string typeName)
        {
            var parts = typeName.Split(',');
            return parts.Length >= 2 ? $"{parts[0].Trim()}, {parts[1].Trim()}" : typeName;
        }
    }
}
