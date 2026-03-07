using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DefValidator.Core;

internal sealed class AssemblyCatalog {
    private readonly Dictionary<string, CatalogType> _typesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, CatalogMember>> _memberCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogType> _syntheticTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _assignableNamesCache = new(StringComparer.Ordinal);

    private AssemblyCatalog() {
    }

    public static AssemblyCatalog Load(ModContext context, DiagnosticBag diagnostics) {
        var gameAssemblyPaths = EnumerateGameManagedAssemblies(context.GameDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var coreMetadataAssemblyPaths = gameAssemblyPaths
            .Where(static path => IsRelevantCoreMetadataAssembly(Path.GetFileName(path)))
            .ToList();
        if (coreMetadataAssemblyPaths.Count == 0) {
            coreMetadataAssemblyPaths = gameAssemblyPaths;
        }
        var modAssemblyPaths = context.ModsInLoadOrder
            .SelectMany(static mod => mod.AssemblyPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var runtimeAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
                                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                                .GroupBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                                .Select(static group => group.First())
                                .ToList()
                                ?? [];

        var resolverAssemblies = gameAssemblyPaths
            .Concat(modAssemblyPaths)
            .Concat(runtimeAssemblies)
            .GroupBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        var catalog = new AssemblyCatalog();
        foreach (var type in LoadCoreTypes(coreMetadataAssemblyPaths, resolverAssemblies, diagnostics)
                     .Concat(LoadAssemblyTypes(modAssemblyPaths, resolverAssemblies, diagnostics))) {
            catalog.Register(type);
        }

        return catalog;
    }

    public CatalogType? FindType(string? typeName) {
        if (string.IsNullOrWhiteSpace(typeName)) {
            return null;
        }

        var key = typeName.Trim();
        if (_typesByName.TryGetValue(key, out var type)) {
            return type;
        }

        return TryCreateSyntheticType(key);
    }

    public bool IsDefType(CatalogType? type) => type is not null && IsAssignableTo(type, "Verse.Def");

    public bool IsCompPropertiesType(CatalogType? type) => type is not null && IsAssignableTo(type, "Verse.CompProperties");

    public IReadOnlyDictionary<string, CatalogMember> GetMembers(CatalogType type) {
        if (_memberCache.TryGetValue(type.DisplayName, out var cached)) {
            return cached;
        }

        var members = new Dictionary<string, CatalogMember>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(type.BaseTypeName) && FindType(type.BaseTypeName) is { } baseType) {
            foreach (var pair in GetMembers(baseType)) {
                members.TryAdd(pair.Key, pair.Value);
            }
        }

        foreach (var pair in type.DeclaredMembers) {
            members[pair.Key] = pair.Value;
        }

        _memberCache[type.DisplayName] = members;
        return members;
    }

    public CatalogType GetMemberType(CatalogMember member) => FindType(member.TypeName) ?? CreateUnknownType(member.TypeName);

    public CatalogType GetListItemType(CatalogMember member) => FindType(member.ListItemTypeName) ?? CreateUnknownType(member.ListItemTypeName ?? member.TypeName);

    public bool IsListType(CatalogMember member) => !string.IsNullOrWhiteSpace(member.ListItemTypeName);

    public bool IsScalar(CatalogType type) {
        return type.IsPrimitive
               || type.IsEnum
               || type.FullName is "System.String" or "System.Decimal" or "System.Type";
    }

    public bool IsAssignableTo(CatalogType candidate, CatalogType target) => IsAssignableTo(candidate, target.DisplayName);

    private bool IsAssignableTo(CatalogType candidate, string targetName) {
        return GetAssignableNames(candidate).Contains(targetName);
    }

    private HashSet<string> GetAssignableNames(CatalogType type) {
        if (_assignableNamesCache.TryGetValue(type.DisplayName, out var cached)) {
            return cached;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectAssignableNames(type, names, new HashSet<string>(StringComparer.Ordinal));
        _assignableNamesCache[type.DisplayName] = names;
        return names;
    }

    private void CollectAssignableNames(CatalogType type, ISet<string> destination, ISet<string> visiting) {
        if (!visiting.Add(type.DisplayName)) {
            return;
        }

        destination.Add(type.DisplayName);
        destination.Add(type.Name);
        if (!string.IsNullOrWhiteSpace(type.FullName)) {
            destination.Add(type.FullName);
        }

        foreach (var interfaceName in type.InterfaceTypeNames) {
            destination.Add(interfaceName);
            if (FindType(interfaceName) is { } interfaceType) {
                CollectAssignableNames(interfaceType, destination, visiting);
            }
        }

        if (!string.IsNullOrWhiteSpace(type.BaseTypeName)) {
            destination.Add(type.BaseTypeName);
            if (FindType(type.BaseTypeName) is { } baseType) {
                CollectAssignableNames(baseType, destination, visiting);
            }
        }
    }

    private void Register(CatalogType type) {
        _typesByName.TryAdd(type.Name, type);
        if (!string.IsNullOrWhiteSpace(type.FullName)) {
            _typesByName.TryAdd(type.FullName, type);
        }
    }

    private CatalogType? TryCreateSyntheticType(string key) {
        if (_syntheticTypes.TryGetValue(key, out var cached)) {
            return cached;
        }

        if (!TryGetKnownSystemType(key, out var created)) {
            return null;
        }

        _syntheticTypes[key] = created;
        if (!string.IsNullOrWhiteSpace(created.FullName)) {
            _typesByName.TryAdd(created.FullName, created);
        }

        _typesByName.TryAdd(created.Name, created);
        return created;
    }

    private static bool TryGetKnownSystemType(string key, out CatalogType type) {
        var normalized = key.Trim();
        var fullName = normalized switch {
            "String" => "System.String",
            "Boolean" => "System.Boolean",
            "Char" => "System.Char",
            "SByte" => "System.SByte",
            "Byte" => "System.Byte",
            "Int16" => "System.Int16",
            "UInt16" => "System.UInt16",
            "Int32" => "System.Int32",
            "UInt32" => "System.UInt32",
            "Int64" => "System.Int64",
            "UInt64" => "System.UInt64",
            "Single" => "System.Single",
            "Double" => "System.Double",
            "Decimal" => "System.Decimal",
            "Type" => "System.Type",
            _ => normalized
        };

        if (fullName is not (
                "System.String" or "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte"
                or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64"
                or "System.UInt64" or "System.Single" or "System.Double" or "System.Decimal" or "System.Type")) {
            type = null!;
            return false;
        }

        type = new CatalogType(
            fullName[(fullName.LastIndexOf('.') + 1)..],
            fullName,
            null,
            [],
            false,
            false,
            false,
            fullName is not ("System.String" or "System.Decimal" or "System.Type"),
            [],
            new Dictionary<string, CatalogMember>(StringComparer.Ordinal));
        return true;
    }

    private static CatalogType CreateUnknownType(string typeName) {
        var normalized = string.IsNullOrWhiteSpace(typeName) ? "Unknown" : typeName.Trim();
        var name = normalized[(normalized.LastIndexOf('.') + 1)..];
        return new CatalogType(name, normalized, null, [], false, false, false, false, [],
            new Dictionary<string, CatalogMember>(StringComparer.Ordinal));
    }

    private static IReadOnlyList<CatalogType> LoadCoreTypes(
        IReadOnlyList<string> gameAssemblyPaths,
        IReadOnlyList<string> resolverAssemblies,
        DiagnosticBag diagnostics) {
        var cachePath = GetCoreCachePath(gameAssemblyPaths);
        if (TryReadCache(cachePath, out var cachedTypes)) {
            return cachedTypes;
        }

        var loadedTypes = LoadAssemblyTypes(gameAssemblyPaths, resolverAssemblies, diagnostics);
        TryWriteCache(cachePath, loadedTypes);
        return loadedTypes;
    }

    private static List<CatalogType> LoadAssemblyTypes(
        IReadOnlyList<string> targetAssemblyPaths,
        IReadOnlyList<string> resolverAssemblies,
        DiagnosticBag diagnostics) {
        if (targetAssemblyPaths.Count == 0) {
            return [];
        }

        var loadContext = new MetadataLoadContext(new PathAssemblyResolver(resolverAssemblies),
            ResolveCoreAssemblyName(resolverAssemblies));
        var assemblies = new List<Assembly>();
        foreach (var path in targetAssemblyPaths) {
            try {
                assemblies.Add(loadContext.LoadFromAssemblyPath(Path.GetFullPath(path)));
            } catch (Exception ex) {
                diagnostics.Add("CTX008", DiagnosticSeverity.Warning,
                    $"Failed to load assembly metadata from {path}: {ex.Message}", ValidationStage.Context, file: path);
            }
        }

        return assemblies
            .SelectMany(SafeGetTypes)
            .Select(CreateType)
            .ToList();
    }

    private static CatalogType CreateType(Type type) {
        var members = new Dictionary<string, CatalogMember>(StringComparer.Ordinal);
        foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public |
                                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
            if (member.MemberType is not (MemberTypes.Field or MemberTypes.Property)) {
                continue;
            }

            if (member is PropertyInfo { CanRead: false }) {
                continue;
            }

            var memberType = GetReflectedMemberType(member);
            var listItemType = TryGetEnumerableItemType(memberType);
            var effectiveType = UnwrapNullableType(memberType);
            var itemTypeName = listItemType is null ? null : GetTypeName(UnwrapNullableType(listItemType));
            var entry = new CatalogMember(member.Name, GetTypeName(effectiveType), itemTypeName);
            members.TryAdd(member.Name, entry);
            foreach (var alias in GetAliases(member)) {
                members.TryAdd(alias, entry);
            }
        }

        return new CatalogType(
            type.Name,
            type.FullName,
            type.BaseType is null ? null : GetTypeName(type.BaseType),
            type.GetInterfaces().Select(GetTypeName).Distinct(StringComparer.Ordinal).ToArray(),
            type.IsAbstract,
            type.IsInterface,
            type.IsEnum,
            type.IsPrimitive,
            type.IsEnum ? SafeGetEnumNames(type) : [],
            members);
    }

    private static string[] SafeGetEnumNames(Type type) {
        try {
            return Enum.GetNames(type);
        } catch {
            return [];
        }
    }

    private static Type GetReflectedMemberType(MemberInfo member) => member switch {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => typeof(object)
    };

    private static Type? TryGetEnumerableItemType(Type type) {
        if (type.IsArray) {
            return type.GetElementType();
        }

        var enumerable = type
            .GetInterfaces()
            .Concat([type])
            .FirstOrDefault(static candidate => candidate.IsGenericType &&
                                                candidate.GetGenericTypeDefinition().FullName ==
                                                "System.Collections.Generic.IEnumerable`1");

        return enumerable?.GetGenericArguments()[0];
    }

    private static Type UnwrapNullableType(Type type) {
        return type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1"
            ? type.GetGenericArguments()[0]
            : type;
    }

    private static IEnumerable<string> GetAliases(MemberInfo member) {
        var aliases = new List<string>();
        try {
            foreach (var attribute in CustomAttributeData.GetCustomAttributes(member)) {
                if (!string.Equals(attribute.AttributeType.Name, "LoadAliasAttribute", StringComparison.Ordinal)
                    && !string.Equals(attribute.AttributeType.FullName, "Verse.LoadAliasAttribute",
                        StringComparison.Ordinal)) {
                    continue;
                }

                if (attribute.ConstructorArguments.Count == 0) {
                    continue;
                }

                var value = attribute.ConstructorArguments[0].Value as string;
                if (!string.IsNullOrWhiteSpace(value)) {
                    aliases.Add(value);
                }
            }
        } catch {
        }

        return aliases;
    }

    private static string GetTypeName(Type type) => type.FullName ?? type.Name;

    private static bool IsRelevantCoreMetadataAssembly(string assemblyName) {
        return assemblyName.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Assembly-CSharp-firstpass.dll", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("UnityEngine.dll", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateGameManagedAssemblies(string gameDirectory) {
        var candidates = new[] {
            Path.Combine(gameDirectory, "Contents", "Resources", "Data", "Managed"),
            Path.Combine(gameDirectory, "Data", "Managed"),
            Path.Combine(gameDirectory, "RimWorldWin64_Data", "Managed"),
            Path.Combine(gameDirectory, "RimWorldLinux_Data", "Managed"),
            Path.Combine(gameDirectory, "RimWorldMac.app", "Contents", "Resources", "Data", "Managed")
        };

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!Directory.Exists(directory)) {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)) {
                yield return filePath;
            }
        }
    }

    private static string ResolveCoreAssemblyName(IReadOnlyList<string> knownAssemblies) {
        if (knownAssemblies.Any(static path =>
                string.Equals(Path.GetFileName(path), "mscorlib.dll", StringComparison.OrdinalIgnoreCase))) {
            return "mscorlib";
        }

        return typeof(object).Assembly.GetName().Name ?? "System.Private.CoreLib";
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly) {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            return ex.Types.Where(static type => type is not null)!;
        }
    }

    private static string GetCoreCachePath(IReadOnlyList<string> gameAssemblyPaths) {
        var builder = new StringBuilder();
        foreach (var path in gameAssemblyPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)) {
            var info = new FileInfo(path);
            builder.Append(Path.GetFullPath(path));
            builder.Append('|');
            builder.Append(info.Length);
            builder.Append('|');
            builder.Append(info.LastWriteTimeUtc.Ticks);
            builder.Append('\n');
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(GetCacheDirectory(), $"core-types-{hash}.json");
    }

    private static string GetCacheDirectory() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows()) {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DefValidator");
        }

        if (OperatingSystem.IsMacOS()) {
            return Path.Combine(home, "Library", "Caches", "DefValidator");
        }

        return Path.Combine(home, ".cache", "defvalidator");
    }

    private static bool TryReadCache(string cachePath, out IReadOnlyList<CatalogType> types) {
        try {
            if (!File.Exists(cachePath)) {
                types = [];
                return false;
            }

            var json = File.ReadAllText(cachePath);
            var cache = JsonSerializer.Deserialize<AssemblyCatalogCache>(json);
            if (cache?.Version != 1 || cache.Types is null) {
                types = [];
                return false;
            }

            types = cache.Types.Select(static type => type.ToCatalogType()).ToList();
            return true;
        } catch {
            types = [];
            return false;
        }
    }

    private static void TryWriteCache(string cachePath, IReadOnlyList<CatalogType> types) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var cache = new AssemblyCatalogCache(1, types.Select(static type => CachedType.From(type)).ToList());
            var json = JsonSerializer.Serialize(cache);
            File.WriteAllText(cachePath, json);
        } catch {
        }
    }

    private sealed record AssemblyCatalogCache(int Version, List<CachedType> Types);

    private sealed record CachedType(
        string Name,
        string? FullName,
        string? BaseTypeName,
        string[] InterfaceTypeNames,
        bool IsAbstract,
        bool IsInterface,
        bool IsEnum,
        bool IsPrimitive,
        string[] EnumNames,
        Dictionary<string, CachedMember> DeclaredMembers) {
        public CatalogType ToCatalogType() {
            return new CatalogType(Name, FullName, BaseTypeName, InterfaceTypeNames, IsAbstract, IsInterface, IsEnum,
                IsPrimitive, EnumNames,
                DeclaredMembers.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToCatalogMember(),
                    StringComparer.Ordinal));
        }

        public static CachedType From(CatalogType type) {
            return new CachedType(type.Name, type.FullName, type.BaseTypeName, type.InterfaceTypeNames.ToArray(),
                type.IsAbstract, type.IsInterface, type.IsEnum, type.IsPrimitive, type.EnumNames.ToArray(),
                type.DeclaredMembers.ToDictionary(static pair => pair.Key, static pair => CachedMember.From(pair.Value),
                    StringComparer.Ordinal));
        }
    }

    private sealed record CachedMember(string Name, string TypeName, string? ListItemTypeName) {
        public CatalogMember ToCatalogMember() => new(Name, TypeName, ListItemTypeName);

        public static CachedMember From(CatalogMember member) => new(member.Name, member.TypeName, member.ListItemTypeName);
    }
}

internal sealed record CatalogType(
    string Name,
    string? FullName,
    string? BaseTypeName,
    IReadOnlyList<string> InterfaceTypeNames,
    bool IsAbstract,
    bool IsInterface,
    bool IsEnum,
    bool IsPrimitive,
    IReadOnlyList<string> EnumNames,
    IReadOnlyDictionary<string, CatalogMember> DeclaredMembers) {
    public string DisplayName => FullName ?? Name;
}

internal sealed record CatalogMember(string Name, string TypeName, string? ListItemTypeName);
