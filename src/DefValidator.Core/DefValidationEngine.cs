using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DefValidator.Core;

public static class DefValidationEngine {
    public static async Task<ValidationResult> ValidateAsync(ValidationOptions options, CancellationToken cancellationToken) {
        var run = await ValidateWithProfileAsync(options, cancellationToken);
        return run.Result;
    }

    public static Task<ValidationRun> ValidateWithProfileAsync(ValidationOptions options, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var timings = new List<ValidationTiming>();
        var total = Stopwatch.StartNew();
        var diagnostics = new DiagnosticBag();

        var context = MeasureValue("build_context", () => ModContextBuilder.Build(options, diagnostics));
        if (context is null) {
            return Task.FromResult(Finish(diagnostics.ToResult()));
        }

        var catalog = MeasureValue("load_metadata", () => AssemblyCatalog.Load(context, diagnostics));
        var aggregate = MeasureValue("build_xml", () => XmlPipeline.BuildAggregate(context, diagnostics));
        MeasureStep("semantic_validate", () => {
            var validator = new SemanticValidator(catalog, diagnostics);
            validator.Validate(aggregate);
        });

        var result = MeasureValue("filter_diagnostics", () => FilterDiagnostics(diagnostics.ToResult(), context));
        return Task.FromResult(Finish(result));

        T MeasureValue<T>(string name, Func<T> action) {
            var stopwatch = Stopwatch.StartNew();
            try {
                return action();
            } finally {
                stopwatch.Stop();
                timings.Add(new ValidationTiming(name, stopwatch.Elapsed));
            }
        }

        void MeasureStep(string name, Action action) {
            var stopwatch = Stopwatch.StartNew();
            try {
                action();
            } finally {
                stopwatch.Stop();
                timings.Add(new ValidationTiming(name, stopwatch.Elapsed));
            }
        }

        ValidationRun Finish(ValidationResult result) {
            total.Stop();
            timings.Add(new ValidationTiming("total", total.Elapsed));
            return new ValidationRun(result, timings);
        }
    }

    private static ValidationResult FilterDiagnostics(ValidationResult result, ModContext context) {
        var targetRoot = Path.GetFullPath(context.TargetMod.RootPath);
        var filtered = result.Diagnostics
            .Where(diagnostic => IsRelevantToTarget(diagnostic, context.TargetMod.PackageId, targetRoot))
            .ToList();

        var summary = new ValidationSummary(
            filtered.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

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
    private const string CorePackageId = "ludeon.rimworld";

    public static XDocument BuildAggregate(ModContext context, DiagnosticBag diagnostics) {
        var aggregate = new XDocument(new XElement("Defs"));

        foreach (var mod in context.ModsInLoadOrder) {
            if (string.Equals(mod.PackageId, CorePackageId, StringComparison.OrdinalIgnoreCase)) {
                AppendAggregate(LoadCoreAggregate(mod, context.ActivePackageIds, diagnostics), aggregate);
                continue;
            }

            AppendModAggregate(mod, aggregate, diagnostics, context.ActivePackageIds);
        }

        return InheritanceResolver.Resolve(aggregate, diagnostics);
    }

    private static XDocument LoadCoreAggregate(
        ModInfo coreMod,
        IReadOnlySet<string> activePackageIds,
        DiagnosticBag diagnostics) {
        var cachePath = GetCoreXmlCachePath(coreMod, activePackageIds);
        if (TryReadCoreCache(cachePath, out var cached)) {
            return cached;
        }

        var aggregate = BuildModAggregate(coreMod, diagnostics, activePackageIds);
        var resolved = InheritanceResolver.Resolve(aggregate, diagnostics);
        var sanitized = SanitizeCachedAggregate(resolved);
        TryWriteCoreCache(cachePath, sanitized);
        return sanitized;
    }

    private static XDocument BuildModAggregate(
        ModInfo mod,
        DiagnosticBag diagnostics,
        IReadOnlySet<string> activePackageIds) {
        var aggregate = new XDocument(new XElement("Defs"));
        AppendModAggregate(mod, aggregate, diagnostics, activePackageIds);
        return aggregate;
    }

    private static void AppendModAggregate(
        ModInfo mod,
        XDocument aggregate,
        DiagnosticBag diagnostics,
        IReadOnlySet<string> activePackageIds) {
        foreach (var folder in mod.LoadFolders) {
            var root = Path.GetFullPath(Path.Combine(mod.RootPath, folder));
            CollectXmlDocuments(mod, root, "Defs", aggregate.Root!, diagnostics, activePackageIds);
        }
    }

    private static void AppendAggregate(XDocument source, XDocument destination) {
        if (source.Root is null) {
            return;
        }

        foreach (var child in source.Root.Elements()) {
            destination.Root!.Add(CloneElement(child));
        }
    }

    private static void CollectXmlDocuments(
        ModInfo mod,
        string root,
        string subfolder,
        XElement destination,
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
                if (document.Root is null) {
                    continue;
                }

                Annotate(document.Root, filePath, mod.PackageId);
                ApplyMayRequire(document.Root, activePackageIds);
                foreach (var child in document.Root.Elements()) {
                    destination.Add(CloneElement(child));
                }
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

    private static XDocument SanitizeCachedAggregate(XDocument document) {
        var root = document.Root is null ? new XElement("Defs") : CloneElement(document.Root);
        foreach (var element in root.DescendantsAndSelf()) {
            element.RemoveAnnotations<SourceInfo>();
            foreach (var attribute in element.Attributes()
                         .Where(static attribute => attribute.Name.LocalName is "ParentName" or "Inherit"
                             or "MayRequire" or "MayRequireAnyOf")
                         .ToList()) {
                attribute.Remove();
            }
        }

        return new XDocument(root);
    }

    private static bool TryReadCoreCache(string cachePath, out XDocument aggregate) {
        return CacheFiles.TryReadXml(cachePath, out aggregate);
    }

    private static void TryWriteCoreCache(string cachePath, XDocument aggregate) {
        CacheFiles.TryWriteXml(cachePath, aggregate);
    }

    private static string GetCoreXmlCachePath(ModInfo coreMod, IReadOnlySet<string> activePackageIds) {
        IEnumerable<string> Fingerprint() {
            yield return "core-xml-v1";

            foreach (var packageId in activePackageIds.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)) {
                yield return $"pkg|{packageId}";
            }

            foreach (var folder in coreMod.LoadFolders) {
                var defsDirectory = Path.Combine(coreMod.RootPath, folder, "Defs");
                if (!Directory.Exists(defsDirectory)) {
                    continue;
                }

                foreach (var filePath in Directory.EnumerateFiles(defsDirectory, "*.xml", SearchOption.AllDirectories)
                             .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)) {
                    var info = new FileInfo(filePath);
                    yield return $"{Path.GetFullPath(filePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                }
            }
        }

        return CacheFiles.BuildPath("core-xml", "xml", Fingerprint());
    }
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
            .GroupBy(static def => $"{def.Type.DisplayName}::{def.DefName}", StringComparer.Ordinal)
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
                catalog.IsAssignableTo(def.Type, reference.TargetType) &&
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

    private void ValidateObject(XElement element, CatalogType declaredType, string defType, string? defName,
        bool isRoot = false) {
        var actualType = ResolveClassOverride(element, declaredType, defType, defName) ?? declaredType;
        var members = catalog.GetMembers(actualType);
        var duplicates = element.Elements()
            .Where(static child => child.Name.LocalName != "li")
            .GroupBy(static child => child.Name.LocalName, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicates) {
            if (members.TryGetValue(duplicate.Key, out var member) && catalog.IsListType(member)) {
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

            ValidateValue(child, member, defType, defName);
        }
    }

    private void ValidateValue(XElement element, CatalogMember member, string defType, string? defName) {
        if (catalog.IsListType(member)) {
            var itemType = catalog.GetListItemType(member);
            foreach (var item in element.Elements()) {
                var effectiveItemType = ResolveClassOverride(item, itemType, defType, defName) ?? itemType;
                if (catalog.IsDefType(effectiveItemType)) {
                    CollectReference(item, effectiveItemType, defType, defName);
                    continue;
                }

                if (catalog.IsScalar(effectiveItemType)) {
                    ValidateScalar(item, effectiveItemType, defType, defName);
                } else {
                    ValidateObject(item, effectiveItemType, defType, defName);
                }
            }

            return;
        }

        var memberType = catalog.GetMemberType(member);
        if (catalog.IsDefType(memberType)) {
            CollectReference(element, memberType, defType, defName);
            return;
        }

        if (catalog.IsScalar(memberType)) {
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

    private void ValidateScalar(XElement element, CatalogType type, string defType, string? defName) {
        var value = (element.Value).Trim();
        var ok = type.IsEnum
            ? type.EnumNames.Any(name => string.Equals(name, value, StringComparison.Ordinal))
            : IsScalarValueAssignable(type, value);

        if (!ok) {
            AddDiagnostic(element, "TYPE005", DiagnosticSeverity.Error,
                $"Value '{value}' is not assignable to {type.Name}.", ValidationStage.Type, defType, defName);
        }
    }

    private bool IsScalarValueAssignable(CatalogType type, string value) {
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

    private CatalogType? ResolveClassOverride(XElement element, CatalogType declaredType, string defType, string? defName) {
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

        if (catalog.IsAssignableTo(resolvedType, declaredType)) {
            return resolvedType;
        }

        AddDiagnostic(element, "TYPE006", DiagnosticSeverity.Error,
            $"Class '{className}' is not assignable to {declaredType.Name}.", ValidationStage.Type, defType,
            defName);

        return null;
    }

    private void CollectReference(XElement element, CatalogType targetType, string defType, string? defName) {
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
        CatalogType TargetType,
        string Value,
        string OwnerDefType,
        string? OwnerDefName);

    private sealed record ResolvedDef(XElement Element, CatalogType Type, string? DefName);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.-]+$",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}

internal sealed record SourceInfo(string? File, int? Line, int? Column, string? PackageId);
