using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DefValidator.Core;

public static class DefValidationEngine {
    public static Task<ValidationResult> ValidateAsync(ValidationOptions options, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new DiagnosticBag();
        var context = ModContextBuilder.Build(options, diagnostics);
        if (context is null) {
            return Task.FromResult(diagnostics.ToResult());
        }

        var catalog = AssemblyCatalog.Load(context, diagnostics);
        var aggregate = XmlPipeline.BuildAggregate(context, diagnostics);

        var validator = new SemanticValidator(catalog, diagnostics);
        validator.Validate(aggregate);
        return Task.FromResult(FilterDiagnostics(diagnostics.ToResult(), context));
    }

    private static ValidationResult FilterDiagnostics(ValidationResult result, ModContext context) {
        var targetRoot = Path.GetFullPath(context.TargetMod.RootPath);
        var filtered = result.Diagnostics
            .Where(diagnostic => IsRelevantToTarget(diagnostic, context.TargetMod.PackageId, targetRoot))
            .ToList();

        var summary = new ValidationSummary(
            filtered.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            filtered.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning));

        return new ValidationResult(summary, filtered);
    }

    private static bool IsRelevantToTarget(Diagnostic diagnostic, string targetPackageId, string targetRoot) {
        if (BlockingContextCodes.Contains(diagnostic.Code)) {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.PackageId) && string.Equals(diagnostic.PackageId, targetPackageId,
                StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return !string.IsNullOrWhiteSpace(diagnostic.File) && IsPathUnder(diagnostic.File, targetRoot);
    }

    private static bool IsPathUnder(string path, string rootPath) {
        var fullPath = Path.GetFullPath(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(normalizedRoot, comparison) || string.Equals(fullPath, rootPath, comparison);
    }

    private static readonly HashSet<string> BlockingContextCodes =
        ["CTX001", "CTX002", "CTX003", "CTX004", "CTX005", "CTX006", "CTX007"];
}

internal static class XmlPipeline {
    public static XDocument BuildAggregate(ModContext context, DiagnosticBag diagnostics) {
        var defDocuments = new List<(ModInfo Mod, string FilePath, XDocument Document)>();

        foreach (var mod in context.ModsInLoadOrder) {
            foreach (var folder in mod.LoadFolders) {
                var root = Path.GetFullPath(Path.Combine(mod.RootPath, folder));
                CollectXmlDocuments(mod, root, "Defs", defDocuments, diagnostics, context.ActivePackageIds);
            }
        }

        var aggregate = new XDocument(new XElement("Defs"));
        foreach (var (_, _, document) in defDocuments) {
            if (document.Root is null) {
                continue;
            }

            foreach (var child in document.Root.Elements()) {
                aggregate.Root!.Add(CloneElement(child));
            }
        }

        return InheritanceResolver.Resolve(aggregate, diagnostics);
    }

    private static void CollectXmlDocuments(
        ModInfo mod,
        string root,
        string subfolder,
        ICollection<(ModInfo Mod, string FilePath, XDocument Document)> destination,
        DiagnosticBag diagnostics,
        IReadOnlySet<string> activePackageIds) {
        var directory = Path.Combine(root, subfolder);
        if (!Directory.Exists(directory)) {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)) {
            try {
                var document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                if (document.Root is not null) {
                    Annotate(document.Root, filePath, mod.PackageId);
                    ApplyMayRequire(document.Root, activePackageIds);
                }

                destination.Add((mod, filePath, document));
            } catch (XmlException ex) {
                diagnostics.Add("XML001", DiagnosticSeverity.Error, ex.Message, ValidationStage.XmlLoad, filePath,
                    ex.LineNumber, ex.LinePosition, mod.PackageId);
            }
        }
    }

    private static void Annotate(XElement root, string filePath, string packageId) {
        foreach (var element in root.DescendantsAndSelf()) {
            var lineInfo = (IXmlLineInfo)element;
            element.AddAnnotation(new SourceInfo(filePath, lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                lineInfo.HasLineInfo() ? lineInfo.LinePosition : null, packageId));
        }
    }

    private static void ApplyMayRequire(XElement root, IReadOnlySet<string> activePackageIds) {
        foreach (var element in root.DescendantsAndSelf().ToList()) {
            var mayRequire = element.Attribute("MayRequire")?.Value;
            var mayRequireAnyOf = element.Attribute("MayRequireAnyOf")?.Value;
            if (IsAllowed(mayRequire, mayRequireAnyOf, activePackageIds)) {
                continue;
            }

            element.Remove();
        }
    }

    private static bool IsAllowed(string? mayRequire, string? mayRequireAnyOf, IReadOnlySet<string> activePackageIds) {
        if (!string.IsNullOrWhiteSpace(mayRequire)) {
            var allPresent = mayRequire
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(activePackageIds.Contains);

            if (!allPresent) return false;
        }

        if (string.IsNullOrWhiteSpace(mayRequireAnyOf)) return true;

        var anyPresent = mayRequireAnyOf
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(activePackageIds.Contains);

        return anyPresent;
    }

    internal static XElement CloneElement(XElement element) {
        var clone = new XElement(element.Name,
            element.Attributes().Select(static attribute => new XAttribute(attribute)),
            element.Nodes().Select(CloneNode).Where(static node => node is not null));

        if (element.Annotation<SourceInfo>() is { } source) {
            clone.AddAnnotation(source);
        }

        return clone;
    }

    internal static XNode? CloneNode(XNode node) => node switch {
        XElement child => CloneElement(child),
        XCData cdata => new XCData(cdata.Value),
        XText text => new XText(text.Value),
        XComment comment => new XComment(comment.Value),
        _ => null
    };
}

internal static class InheritanceResolver {
    public static XDocument Resolve(XDocument aggregate, DiagnosticBag diagnostics) {
        var root = aggregate.Root ?? new XElement("Defs");
        var nodes = root.Elements().Select((element, index) => new NodeEntry(element, index)).ToList();
        var named = nodes
            .Where(static node => !string.IsNullOrWhiteSpace(node.Name))
            .GroupBy(static node => node.Name!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key,
                static group => group.OrderBy(static node => node.LoadOrder).ToList(), StringComparer.Ordinal);

        var resolved = new Dictionary<XElement, XElement>();
        var visiting = new HashSet<XElement>();

        var newRoot = new XElement("Defs", nodes.Select(ResolveNode));
        return new XDocument(newRoot);

        XElement ResolveNode(NodeEntry node) {
            if (resolved.TryGetValue(node.Element, out var existing)) {
                return existing;
            }

            if (!visiting.Add(node.Element)) {
                var source = node.Element.Annotation<SourceInfo>();
                diagnostics.Add("INHERIT002", DiagnosticSeverity.Error, "Cyclic inheritance hierarchy detected.",
                    ValidationStage.Inheritance, source?.File, source?.Line, source?.Column, source?.PackageId);
                return XmlPipeline.CloneElement(node.Element);
            }

            XElement result;
            if (string.IsNullOrWhiteSpace(node.ParentName)) {
                result = XmlPipeline.CloneElement(node.Element);
            } else if (!named.TryGetValue(node.ParentName, out var candidates)) {
                var source = node.Element.Annotation<SourceInfo>();
                diagnostics.Add("INHERIT001", DiagnosticSeverity.Error,
                    $"Could not find ParentName '{node.ParentName}'.", ValidationStage.Inheritance, source?.File,
                    source?.Line, source?.Column, source?.PackageId, node.Element.Name.LocalName);
                result = XmlPipeline.CloneElement(node.Element);
            } else {
                var parent = candidates.Where(candidate => candidate.LoadOrder <= node.LoadOrder)
                    .OrderByDescending(static candidate => candidate.LoadOrder).FirstOrDefault();
                if (parent is null) {
                    var source = node.Element.Annotation<SourceInfo>();
                    diagnostics.Add("INHERIT001", DiagnosticSeverity.Error,
                        $"Could not find ParentName '{node.ParentName}'.", ValidationStage.Inheritance, source?.File,
                        source?.Line, source?.Column, source?.PackageId, node.Element.Name.LocalName);
                    result = XmlPipeline.CloneElement(node.Element);
                } else {
                    result = Merge(ResolveNode(parent), node.Element, diagnostics);
                }
            }

            visiting.Remove(node.Element);
            resolved[node.Element] = result;
            return result;
        }
    }

    private static XElement Merge(XElement parent, XElement child, DiagnosticBag diagnostics) {
        CheckDuplicateChildNames(child, diagnostics);
        var clone = XmlPipeline.CloneElement(parent);
        MergeInto(clone, child);
        return clone;
    }

    private static void MergeInto(XElement current, XElement child) {
        var inheritAttribute = child.Attribute("Inherit")?.Value;
        if (string.Equals(inheritAttribute, "false", StringComparison.OrdinalIgnoreCase)) {
            current.ReplaceAttributes(child.Attributes()
                .Where(static attribute => attribute.Name.LocalName != "Inherit")
                .Select(static attribute => new XAttribute(attribute)));
            current.ReplaceNodes(child.Nodes().Select(XmlPipeline.CloneNode).Where(static node => node is not null));
            CopySource(current, child);
            return;
        }

        current.ReplaceAttributes(child.Attributes().Select(static attribute => new XAttribute(attribute)));
        CopySource(current, child);

        var elementChildren = child.Elements().ToList();
        var textValue = string.Concat(child.Nodes().OfType<XText>().Select(static text => text.Value));
        if (elementChildren.Count == 0) {
            if (textValue.Length > 0) {
                current.Value = textValue;
            }

            return;
        }

        foreach (var childElement in elementChildren) {
            if (childElement.Name.LocalName == "li") {
                current.Add(XmlPipeline.CloneElement(childElement));
                continue;
            }

            var existing = current.Elements(childElement.Name).FirstOrDefault();
            if (existing is null) {
                current.Add(XmlPipeline.CloneElement(childElement));
                continue;
            }

            MergeInto(existing, childElement);
        }
    }

    private static void CheckDuplicateChildNames(XElement element, DiagnosticBag diagnostics) {
        var duplicates = element.Elements()
            .Where(static child => child.Name.LocalName != "li")
            .GroupBy(static child => child.Name.LocalName, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicates) {
            var source = duplicate.First().Annotation<SourceInfo>();
            diagnostics.Add("INHERIT003", DiagnosticSeverity.Error,
                $"Duplicate XML node name '{duplicate.Key}' in inherited block.", ValidationStage.Inheritance,
                source?.File, source?.Line, source?.Column, source?.PackageId, element.Name.LocalName);
        }
    }

    private static void CopySource(XElement destination, XElement sourceElement) {
        if (sourceElement.Annotation<SourceInfo>() is not { } source) return;

        destination.RemoveAnnotations<SourceInfo>();
        destination.AddAnnotation(source);
    }

    private sealed record NodeEntry(XElement Element, int LoadOrder) {
        public string? Name => Element.Attribute("Name")?.Value;

        public string? ParentName => Element.Attribute("ParentName")?.Value;
    }
}

internal sealed partial class SemanticValidator(AssemblyCatalog catalog, DiagnosticBag diagnostics) {
    private static readonly System.Text.RegularExpressions.Regex DefNamePattern = MyRegex();

    private readonly List<PendingReference> _references = [];
    private readonly List<ResolvedDef> _defs = [];

    public void Validate(XDocument aggregate) {
        foreach (var element in aggregate.Root?.Elements() ?? []) {
            ValidateRootDef(element);
        }

        var duplicates = _defs
            .Where(static def => !string.IsNullOrWhiteSpace(def.DefName))
            .GroupBy(static def => $"{def.Type.FullName ?? def.Type.Name}::{def.DefName}", StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicates) {
            foreach (var def in duplicate) {
                AddDiagnostic(def.Element, "XREF001", DiagnosticSeverity.Error,
                    $"Duplicate defName '{def.DefName}' for type {def.Type.Name}.", ValidationStage.Xref, def.Type.Name,
                    def.DefName);
            }
        }

        foreach (var reference in _references) {
            var matched = _defs.Any(def =>
                AssemblyCatalog.IsAssignableTo(def.Type, reference.TargetType) &&
                string.Equals(def.DefName, reference.Value, StringComparison.Ordinal));
            if (!matched) {
                diagnostics.Add("XREF002", DiagnosticSeverity.Error,
                    $"Could not resolve Def reference '{reference.Value}' for expected type {reference.TargetType.Name}.",
                    ValidationStage.Xref, reference.Source.File, reference.Source.Line, reference.Source.Column,
                    reference.Source.PackageId, reference.OwnerDefType, reference.OwnerDefName);
            }
        }
    }

    private void ValidateRootDef(XElement element) {
        var typeName = element.Attribute("Class")?.Value ?? element.Name.LocalName;
        var type = catalog.FindType(typeName);
        if (type is null) {
            AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Unknown Def class '{typeName}'.",
                ValidationStage.Type, element.Name.LocalName, GetDefName(element));
            return;
        }

        if (!catalog.IsDefType(type)) {
            AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Type '{typeName}' is not a Verse.Def.",
                ValidationStage.Type, element.Name.LocalName, GetDefName(element));
            return;
        }

        var defName = GetDefName(element);
        if (string.IsNullOrWhiteSpace(defName)) {
            AddDiagnostic(element, "RULE001", DiagnosticSeverity.Error, "Missing defName.", ValidationStage.Rule,
                element.Name.LocalName, defName);
        } else if (!DefNamePattern.IsMatch(defName)) {
            AddDiagnostic(element, "RULE002", DiagnosticSeverity.Error, $"Invalid defName '{defName}'.",
                ValidationStage.Rule, element.Name.LocalName, defName);
        }

        ValidateObject(element, type, element.Name.LocalName, defName, isRoot: true);
        _defs.Add(new ResolvedDef(element, type, defName));
    }

    private void ValidateObject(XElement element, Type declaredType, string defType, string? defName,
        bool isRoot = false) {
        var actualType = ResolveClassOverride(element, declaredType, defType, defName) ?? declaredType;
        var members = catalog.GetMembers(actualType);
        var duplicates = element.Elements()
            .Where(static child => child.Name.LocalName != "li")
            .GroupBy(static child => child.Name.LocalName, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicates) {
            if (members.TryGetValue(duplicate.Key, out var member) &&
                AssemblyCatalog.IsListType(AssemblyCatalog.GetMemberType(member), out _)) {
                continue;
            }

            var duplicateElement = duplicate.First();
            AddDiagnostic(duplicateElement, "TYPE003", DiagnosticSeverity.Error, $"Duplicate node '{duplicate.Key}'.",
                ValidationStage.Type, defType, defName);
        }

        foreach (var child in element.Elements()) {
            if (!members.TryGetValue(child.Name.LocalName, out var member)) {
                if (!isRoot || child.Name.LocalName != "defName") {
                    AddDiagnostic(child, "TYPE002", DiagnosticSeverity.Error,
                        $"Unknown node '{child.Name.LocalName}' on type {actualType.Name}.", ValidationStage.Type,
                        defType, defName);
                }

                continue;
            }

            var memberType = AssemblyCatalog.GetMemberType(member);
            ValidateValue(child, memberType, defType, defName);
        }
    }

    private void ValidateValue(XElement element, Type memberType, string defType, string? defName) {
        memberType = AssemblyCatalog.UnwrapNullable(memberType);
        if (AssemblyCatalog.IsListType(memberType, out var itemType)) {
            foreach (var item in element.Elements()) {
                var effectiveItemType = ResolveClassOverride(item, itemType, defType, defName) ?? itemType;
                if (catalog.IsDefType(effectiveItemType)) {
                    CollectReference(item, effectiveItemType, defType, defName);
                    continue;
                }

                if (AssemblyCatalog.IsScalar(effectiveItemType)) {
                    ValidateScalar(item, effectiveItemType, defType, defName);
                } else {
                    ValidateObject(item, effectiveItemType, defType, defName);
                }
            }

            return;
        }

        if (catalog.IsDefType(memberType)) {
            CollectReference(element, memberType, defType, defName);
            return;
        }

        if (AssemblyCatalog.IsScalar(memberType)) {
            ValidateScalar(element, memberType, defType, defName);
            return;
        }

        if ((memberType.IsAbstract || memberType.IsInterface) && element.Attribute("Class") is null) {
            AddDiagnostic(element, "TYPE004", DiagnosticSeverity.Error,
                $"Type {memberType.Name} requires a Class attribute.", ValidationStage.Type, defType, defName);
            return;
        }

        ValidateObject(element, memberType, defType, defName);
    }

    private void ValidateScalar(XElement element, Type type, string defType, string? defName) {
        var value = (element.Value).Trim();
        var ok = type.IsEnum
            ? Enum.GetNames(type).Any(name => string.Equals(name, value, StringComparison.Ordinal))
            : IsScalarValueAssignable(type, value);

        if (!ok) {
            AddDiagnostic(element, "TYPE005", DiagnosticSeverity.Error,
                $"Value '{value}' is not assignable to {type.Name}.", ValidationStage.Type, defType, defName);
        }
    }

    private bool IsScalarValueAssignable(Type type, string value) {
        return type.FullName switch {
            "System.String" => true,
            "System.Boolean" => bool.TryParse(value, out _),
            "System.Char" => value.Length == 1,
            "System.SByte" => sbyte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.Byte" => byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.Int16" => short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.UInt16" => ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.Int32" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.UInt32" => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.Int64" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.UInt64" => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "System.Single" => float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out _),
            "System.Double" => double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out _),
            "System.Decimal" => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            "System.Type" => catalog.FindType(value) is not null,
            _ when type.IsPrimitive => value.Length > 0,
            _ => false
        };
    }

    private Type? ResolveClassOverride(XElement element, Type declaredType, string defType, string? defName) {
        var className = element.Attribute("Class")?.Value;
        if (string.IsNullOrWhiteSpace(className)) {
            return null;
        }

        var resolvedType = catalog.FindType(className);
        if (resolvedType is null) {
            AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Unknown Class '{className}'.",
                ValidationStage.Type, defType, defName);
            return null;
        }

        if (AssemblyCatalog.IsAssignableTo(resolvedType, declaredType)) {
            return resolvedType;
        }

        AddDiagnostic(element, "TYPE006", DiagnosticSeverity.Error,
            $"Class '{className}' is not assignable to {declaredType.Name}.", ValidationStage.Type, defType,
            defName);

        return null;
    }

    private void CollectReference(XElement element, Type targetType, string defType, string? defName) {
        var value = (element.Value).Trim();
        if (string.IsNullOrWhiteSpace(value)) {
            AddDiagnostic(element, "XREF003", DiagnosticSeverity.Error,
                $"Empty Def reference for type {targetType.Name}.", ValidationStage.Xref, defType, defName);
            return;
        }

        _references.Add(new PendingReference(element.Annotation<SourceInfo>() ?? new SourceInfo(null, null, null, null),
            targetType, value, defType, defName));
    }

    private void AddDiagnostic(XElement element, string code, DiagnosticSeverity severity, string message,
        ValidationStage stage, string? defType, string? defName) {
        var source = element.Annotation<SourceInfo>();
        diagnostics.Add(code, severity, message, stage, source?.File, source?.Line, source?.Column, source?.PackageId,
            defType, defName);
    }

    private static string? GetDefName(XElement element) => element.Elements()
        .FirstOrDefault(static child => child.Name.LocalName == "defName")?.Value.Trim();

    private sealed record PendingReference(
        SourceInfo Source,
        Type TargetType,
        string Value,
        string OwnerDefType,
        string? OwnerDefName);

    private sealed record ResolvedDef(XElement Element, Type Type, string? DefName);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.-]+$",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}

internal sealed record SourceInfo(string? File, int? Line, int? Column, string? PackageId);