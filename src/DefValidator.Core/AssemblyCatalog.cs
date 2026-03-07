using System.Reflection;

namespace DefValidator.Core;

internal sealed class AssemblyCatalog {
    private readonly Dictionary<string, Type> _typesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, IReadOnlyDictionary<string, MemberInfo>> _memberCache = new();
    private readonly MetadataLoadContext? _loadContext;

    private AssemblyCatalog(MetadataLoadContext? loadContext, Type? defBaseType, Type? compPropertiesBaseType) {
        _loadContext = loadContext;
        DefBaseType = defBaseType;
        CompPropertiesBaseType = compPropertiesBaseType;
    }

    private Type? DefBaseType { get; }

    private Type? CompPropertiesBaseType { get; }

    public static AssemblyCatalog Load(ModContext context, DiagnosticBag diagnostics) {
        var assemblyPaths = EnumerateGameManagedAssemblies(context.GameDirectory)
            .Concat(context.ModsInLoadOrder.SelectMany(static mod => mod.AssemblyPaths))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var runtimeAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
                                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                                .GroupBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                                .Select(static group => group.First())
                                .ToDictionary(static path => Path.GetFileName(path), static path => path,
                                    StringComparer.OrdinalIgnoreCase)
                                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var knownAssemblies = assemblyPaths
            .Concat(runtimeAssemblies.Values)
            .GroupBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        var loadContext = new MetadataLoadContext(new PathAssemblyResolver(knownAssemblies),
            ResolveCoreAssemblyName(knownAssemblies));
        var assemblies = new List<Assembly>();
        foreach (var path in assemblyPaths) {
            try {
                assemblies.Add(loadContext.LoadFromAssemblyPath(Path.GetFullPath(path)));
            } catch (Exception ex) {
                diagnostics.Add("CTX008", DiagnosticSeverity.Warning,
                    $"Failed to load assembly metadata from {path}: {ex.Message}", ValidationStage.Context, file: path);
            }
        }

        var allTypes = assemblies.SelectMany(SafeGetTypes).ToList();
        var catalog = new AssemblyCatalog(
            loadContext,
            allTypes.FirstOrDefault(static type => type.FullName == "Verse.Def"),
            allTypes.FirstOrDefault(static type => type.FullName == "Verse.CompProperties"));

        foreach (var type in allTypes) {
            catalog._typesByName.TryAdd(type.Name, type);
            if (!string.IsNullOrWhiteSpace(type.FullName)) {
                catalog._typesByName.TryAdd(type.FullName!, type);
            }
        }

        return catalog;
    }

    public Type? FindType(string? typeName) {
        if (string.IsNullOrWhiteSpace(typeName)) {
            return null;
        }

        _typesByName.TryGetValue(typeName.Trim(), out var type);
        return type;
    }

    public bool IsDefType(Type? type) =>
        type is not null && DefBaseType is not null && IsAssignableTo(type, DefBaseType);

    public bool IsCompPropertiesType(Type? type) => type is not null && CompPropertiesBaseType is not null &&
                                                    IsAssignableTo(type, CompPropertiesBaseType);

    public IReadOnlyDictionary<string, MemberInfo> GetMembers(Type type) {
        if (_memberCache.TryGetValue(type, out var cached)) {
            return cached;
        }

        var members = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType) {
            foreach (var member in current.GetMembers(BindingFlags.Instance | BindingFlags.Public |
                                                      BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                if (member.MemberType is not (MemberTypes.Field or MemberTypes.Property)) {
                    continue;
                }

                if (member is PropertyInfo { CanRead: false }) {
                    continue;
                }

                members.TryAdd(member.Name, member);
                foreach (var alias in GetAliases(member)) {
                    members.TryAdd(alias, member);
                }
            }
        }

        _memberCache[type] = members;
        return members;
    }

    public static Type GetMemberType(MemberInfo member) => member switch {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => typeof(object)
    };


    private static IEnumerable<string> GetAliases(MemberInfo member) {
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
                yield return value;
            }
        }
    }

    public static bool IsListType(Type type, out Type itemType) {
        if (type.IsArray) {
            itemType = type.GetElementType()!;
            return true;
        }

        var enumerable = type
            .GetInterfaces()
            .Concat([type])
            .FirstOrDefault(static candidate => candidate.IsGenericType &&
                                                candidate.GetGenericTypeDefinition().FullName ==
                                                "System.Collections.Generic.IEnumerable`1");

        if (enumerable is not null) {
            itemType = enumerable.GetGenericArguments()[0];
            return true;
        }

        itemType = typeof(object);
        return false;
    }

    public static Type UnwrapNullable(Type type) {
        return type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1"
            ? type.GetGenericArguments()[0]
            : type;
    }

    public static bool IsScalar(Type type) {
        type = UnwrapNullable(type);
        return type.IsPrimitive
               || type.IsEnum
               || type.FullName is "System.String" or "System.Decimal" or "System.Type";
    }

    public static bool IsAssignableTo(Type candidate, Type target) {
        if (candidate == target || candidate.FullName == target.FullName) {
            return true;
        }

        if (target.IsAssignableFrom(candidate)) {
            return true;
        }

        return candidate.BaseType is not null && IsAssignableTo(candidate.BaseType, target);
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
}