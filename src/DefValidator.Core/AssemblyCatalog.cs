using System.Reflection;
using System.Runtime.Loader;

namespace DefValidator.Core;

internal sealed class AssemblyCatalog
{
    private readonly Dictionary<string, Type> _typesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, IReadOnlyDictionary<string, MemberInfo>> _memberCache = new();

    private AssemblyCatalog(Type? defBaseType, Type? compPropertiesBaseType)
    {
        DefBaseType = defBaseType;
        CompPropertiesBaseType = compPropertiesBaseType;
    }

    public Type? DefBaseType { get; }

    public Type? CompPropertiesBaseType { get; }


    public static AssemblyCatalog Load(ModContext context, DiagnosticBag diagnostics)
    {
        var assemblyPaths = context.ModsInLoadOrder
            .SelectMany(static mod => mod.AssemblyPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var runtimeAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .GroupBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToDictionary(static path => Path.GetFileName(path), static path => path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var knownAssemblies = assemblyPaths
            .Concat(runtimeAssemblies.Values)
            .GroupBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToDictionary(static path => Path.GetFileName(path), static path => path, StringComparer.OrdinalIgnoreCase);

        var loadContext = new MetadataAssemblyLoadContext(knownAssemblies);
        var assemblies = new List<Assembly>();
        foreach (var path in assemblyPaths)
        {
            try
            {
                assemblies.Add(loadContext.LoadFromAssemblyPath(Path.GetFullPath(path)));
            }
            catch (Exception ex)
            {
                diagnostics.Add("CTX008", DiagnosticSeverity.Warning, $"Failed to load assembly metadata from {path}: {ex.Message}", ValidationStage.Context, file: path);
            }
        }

        var catalog = new AssemblyCatalog(
            assemblies.SelectMany(SafeGetTypes).FirstOrDefault(static type => type.FullName == "Verse.Def"),
            assemblies.SelectMany(SafeGetTypes).FirstOrDefault(static type => type.FullName == "Verse.CompProperties"));

        foreach (var assembly in assemblies)
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                catalog._typesByName.TryAdd(type.Name, type);
                if (!string.IsNullOrWhiteSpace(type.FullName))
                {
                    catalog._typesByName.TryAdd(type.FullName!, type);
                }
            }
        }

        return catalog;
    }

    public Type? FindType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        _typesByName.TryGetValue(typeName.Trim(), out var type);
        return type;
    }

    public bool IsDefType(Type? type) => type is not null && DefBaseType is not null && IsAssignableTo(type, DefBaseType);

    public bool IsCompPropertiesType(Type? type) => type is not null && CompPropertiesBaseType is not null && IsAssignableTo(type, CompPropertiesBaseType);


    public IReadOnlyDictionary<string, MemberInfo> GetMembers(Type type)
    {
        if (_memberCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var members = type
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(static member => member.MemberType is MemberTypes.Field or MemberTypes.Property)
            .Where(static member => member is not PropertyInfo property || property.CanRead)
            .GroupBy(static member => member.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        _memberCache[type] = members;
        return members;
    }

    public static Type GetMemberType(MemberInfo member) => member switch
    {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => typeof(object)
    };

    public static bool IsListType(Type type, out Type itemType)
    {
        if (type.IsArray)
        {
            itemType = type.GetElementType()!;
            return true;
        }

        var enumerable = type
            .GetInterfaces()
            .Concat([type])
            .FirstOrDefault(static candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerable is not null)
        {
            itemType = enumerable.GetGenericArguments()[0];
            return true;
        }

        itemType = typeof(object);
        return false;
    }

    public static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    public static bool IsScalar(Type type)
    {
        type = UnwrapNullable(type);
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Type);
    }

    public static bool IsAssignableTo(Type candidate, Type target)
    {
        if (target.IsAssignableFrom(candidate))
        {
            return true;
        }

        return candidate.FullName == target.FullName || candidate.BaseType is not null && IsAssignableTo(candidate.BaseType, target);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null)!;
        }
    }

    private sealed class MetadataAssemblyLoadContext(IReadOnlyDictionary<string, string> knownAssemblies) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (knownAssemblies.TryGetValue($"{assemblyName.Name}.dll", out var path) && File.Exists(path))
            {
                return LoadFromAssemblyPath(path);
            }

            return null;
        }
    }
}
