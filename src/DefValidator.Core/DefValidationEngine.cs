using System.Diagnostics;
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

        var validator = new SemanticValidator(catalog, diagnostics, context.TargetMod.PackageId);
        validator.Validate(aggregate);
        return Task.FromResult(FilterDiagnostics(diagnostics.ToResult(), context));
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

        var metadataProfiler = new StepProfiler();
        var catalog = MeasureValue("load_metadata", () => AssemblyCatalog.Load(context, diagnostics, metadataProfiler));
        timings.AddRange(metadataProfiler.Export("load_metadata"));
        var xmlProfiler = new StepProfiler();
        var aggregate = MeasureValue("build_xml", () => XmlPipeline.BuildAggregate(context, diagnostics, xmlProfiler));
        timings.AddRange(xmlProfiler.Export("build_xml"));
        var semanticProfiler = new StepProfiler();
        MeasureStep("semantic_validate", () => {
            var validator = new SemanticValidator(catalog, diagnostics, context.TargetMod.PackageId, semanticProfiler);
            validator.Validate(aggregate);
        });
        timings.AddRange(semanticProfiler.Export("semantic_validate"));

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

    public static XDocument BuildAggregate(ModContext context, DiagnosticBag diagnostics, StepProfiler? profiler = null) {
        var aggregate = new XDocument(new XElement("Defs"));

        foreach (var mod in context.ModsInLoadOrder) {
            if (string.Equals(mod.PackageId, CorePackageId, StringComparison.OrdinalIgnoreCase)) {
                Measure("load_core_cache", () => AppendAggregate(LoadCoreAggregate(mod, context.ActivePackageIds, diagnostics, profiler), aggregate));
                continue;
            }

            Measure("append_mod_aggregate", () => AppendModAggregate(mod, aggregate, diagnostics, context.ActivePackageIds));
        }

        return MeasureValue("resolve_inheritance", () => InheritanceResolver.Resolve(aggregate, diagnostics));

        T MeasureValue<T>(string name, Func<T> action) {
            if (profiler is null) {
                return action();
            }

            return profiler.MeasureValue(name, action);
        }

        void Measure(string name, Action action) {
            if (profiler is null) {
                action();
                return;
            }

            profiler.Measure(name, action);
        }
    }

    private static XDocument LoadCoreAggregate(
        ModInfo coreMod,
        IReadOnlySet<string> activePackageIds,
        DiagnosticBag diagnostics,
        StepProfiler? profiler = null) {
        var cachePath = GetCoreXmlCachePath(coreMod, activePackageIds);
        var cached = new XDocument(new XElement("Defs"));
        if (MeasureValue("read_core_cache", () => TryReadCoreCache(cachePath, out cached))) {
            return cached;
        }

        var aggregate = MeasureValue("build_core_aggregate", () => BuildModAggregate(coreMod, diagnostics, activePackageIds));
        var resolved = MeasureValue("resolve_core_inheritance", () => InheritanceResolver.Resolve(aggregate, diagnostics));
        var sanitized = MeasureValue("sanitize_core_cache", () => SanitizeCachedAggregate(resolved));
        Measure("write_core_cache", () => TryWriteCoreCache(cachePath, sanitized));
        return sanitized;

        T MeasureValue<T>(string name, Func<T> action) {
            if (profiler is null) {
                return action();
            }

            return profiler.MeasureValue(name, action);
        }

        void Measure(string name, Action action) {
            if (profiler is null) {
                action();
                return;
            }

            profiler.Measure(name, action);
        }
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

        foreach (var child in source.Root.Elements().ToList()) {
            child.Remove();
            destination.Root!.Add(child);
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
                foreach (var child in document.Root.Elements().ToList()) {
                    child.Remove();
                    destination.Add(child);
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
        var root = document.Root ?? new XElement("Defs");
        foreach (var element in root.DescendantsAndSelf()) {
            element.RemoveAnnotations<SourceInfo>();
            foreach (var attribute in element.Attributes()
                         .Where(static attribute => attribute.Name.LocalName is "ParentName" or "Inherit"
                             or "MayRequire" or "MayRequireAnyOf")
                         .ToList()) {
                attribute.Remove();
            }
        }

        return ReferenceEquals(document.Root, root) ? document : new XDocument(root);
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

        var newRoot = new XElement("Defs");
        foreach (var node in nodes) {
            newRoot.Add(XmlPipeline.CloneElement(ResolveNode(node)));
        }

        return new XDocument(newRoot);

        XElement ResolveNode(NodeEntry node) {
            if (resolved.TryGetValue(node.Element, out var existing)) {
                return existing;
            }

            if (!visiting.Add(node.Element)) {
                var source = node.Element.Annotation<SourceInfo>();
                diagnostics.Add("INHERIT002", DiagnosticSeverity.Error, "Cyclic inheritance hierarchy detected.",
                    ValidationStage.Inheritance, source?.File, source?.Line, source?.Column, source?.PackageId);
                return node.Element;
            }

            XElement result;
            if (string.IsNullOrWhiteSpace(node.ParentName)) {
                result = node.Element;
            } else if (!named.TryGetValue(node.ParentName, out var candidates)) {
                var source = node.Element.Annotation<SourceInfo>();
                diagnostics.Add("INHERIT001", DiagnosticSeverity.Error,
                    $"Could not find ParentName '{node.ParentName}'.", ValidationStage.Inheritance, source?.File,
                    source?.Line, source?.Column, source?.PackageId, node.Element.Name.LocalName);
                result = node.Element;
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
            current.ReplaceNodes(child.Nodes().Select(CloneNodePreservingSource).Where(static node => node is not null)!);
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

    private static XNode? CloneNodePreservingSource(XNode node) => XmlPipeline.CloneNode(node);

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
internal sealed partial class SemanticValidator(AssemblyCatalog catalog, DiagnosticBag diagnostics, string targetPackageId, StepProfiler? profiler = null) {
    private static readonly System.Text.RegularExpressions.Regex DefNamePattern = MyRegex();

    private readonly List<PendingReference> _references = [];
    private readonly List<ResolvedDef> _defs = [];

    public void Validate(XDocument aggregate) {
        Measure("validate_root_defs", () => {
            foreach (var element in aggregate.Root?.Elements() ?? []) {
                ValidateRootDef(element);
            }
        });

        Measure("duplicate_defnames", () => {
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
        });

        Measure("resolve_references", () => {
            var defsByName = MeasureValue("index_defs_by_name", BuildDefsByNameIndex);
            foreach (var reference in _references) {
                var matched = MeasureValue("resolve_reference_match", () => {
                    if (!defsByName.TryGetValue(reference.Value, out var candidates)) {
                        return false;
                    }

                    return candidates.Any(def => catalog.IsAssignableTo(def.Type, reference.TargetType));
                });
                if (!matched) {
                    diagnostics.Add("XREF002", DiagnosticSeverity.Error,
                        $"Could not resolve Def reference '{reference.Value}' for expected type {reference.TargetType.Name}.",
                        ValidationStage.Xref, reference.Source.File, reference.Source.Line, reference.Source.Column,
                        reference.Source.PackageId, reference.OwnerDefType, reference.OwnerDefName);
                }
            }
        });
    }

    private Dictionary<string, List<ResolvedDef>> BuildDefsByNameIndex() {
        return _defs
            .Where(static def => !string.IsNullOrWhiteSpace(def.DefName))
            .GroupBy(static def => def.DefName!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
    }

    private void ValidateRootDef(XElement element) {
        var packageId = element.Annotation<SourceInfo>()?.PackageId;
        var shouldValidate = string.Equals(packageId, targetPackageId, StringComparison.OrdinalIgnoreCase);
        var isAbstract = HasTrueAttribute(element, "Abstract");
        var typeName = element.Attribute("Class")?.Value ?? element.Name.LocalName;
        var type = MeasureValue("root_find_type", () => catalog.FindType(typeName));
        if (type is null) {
            if (shouldValidate) {
                AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Unknown Def class '{typeName}'.",
                    ValidationStage.Type, element.Name.LocalName, GetDefName(element));
            }

            return;
        }

        if (!catalog.IsDefType(type)) {
            if (shouldValidate) {
                AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Type '{typeName}' is not a Verse.Def.",
                    ValidationStage.Type, element.Name.LocalName, GetDefName(element));
            }

            return;
        }

        var defName = GetDefName(element);
        if (shouldValidate) {
            if (!isAbstract) {
                if (string.IsNullOrWhiteSpace(defName)) {
                    AddDiagnostic(element, "RULE001", DiagnosticSeverity.Error, "Missing defName.", ValidationStage.Rule,
                        element.Name.LocalName, defName);
                } else if (!DefNamePattern.IsMatch(defName)) {
                    AddDiagnostic(element, "RULE002", DiagnosticSeverity.Error, $"Invalid defName '{defName}'.",
                        ValidationStage.Rule, element.Name.LocalName, defName);
                }
            }

            ValidateObject(element, type, element.Name.LocalName, defName, isRoot: true);
        }

        if (!isAbstract) {
            _defs.Add(new ResolvedDef(element, type, defName));
        }
    }

    private void ValidateObject(XElement element, CatalogType declaredType, string defType, string? defName,
        bool isRoot = false, CatalogType? resolvedType = null) {
        Measure("validate_object", () => {
        var actualType = resolvedType ?? ResolveClassOverride(element, declaredType, defType, defName) ?? declaredType;
        var members = MeasureValue("get_members", () => catalog.GetMembers(actualType));
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
        });
    }

    private void ValidateValue(XElement element, CatalogMember member, string defType, string? defName) {
        Measure("validate_value", () => {
        if (catalog.IsListType(member)) {
            var itemType = catalog.GetListItemType(member);
            foreach (var item in element.Elements()) {
                var effectiveItemType = ResolveClassOverride(item, itemType, defType, defName) ?? itemType;
                if (catalog.IsDefType(effectiveItemType)) {
                    CollectReference(item, effectiveItemType, defType, defName);
                    continue;
                }

                if (CanValidateAsLeafText(item, effectiveItemType)) {
                    ValidateScalar(item, effectiveItemType, defType, defName);
                } else {
                    ValidateObject(item, itemType, defType, defName, resolvedType: effectiveItemType);
                }
            }

            return;
        }

        var memberType = MeasureValue("get_member_type", () => catalog.GetMemberType(member));
        if (catalog.IsDefType(memberType)) {
            CollectReference(element, memberType, defType, defName);
            return;
        }

        if (catalog.IsScalar(memberType)) {
            ValidateScalar(element, memberType, defType, defName);
            return;
        }

        var resolvedMemberType = ResolveClassOverride(element, memberType, defType, defName) ?? memberType;
        if (CanValidateAsLeafText(element, resolvedMemberType)) {
            ValidateScalar(element, resolvedMemberType, defType, defName);
            return;
        }

        if ((memberType.IsAbstract || memberType.IsInterface)
            && element.Attribute("Class") is null
            && ReferenceEquals(resolvedMemberType, memberType)) {
            AddDiagnostic(element, "TYPE004", DiagnosticSeverity.Error,
                $"Type {memberType.Name} requires a Class attribute.", ValidationStage.Type, defType, defName);
            return;
        }

        ValidateObject(element, memberType, defType, defName, resolvedType: resolvedMemberType);
        });
    }

    private void ValidateScalar(XElement element, CatalogType type, string defType, string? defName) {
        Measure("validate_scalar", () => {
        var value = (element.Value).Trim();
        var ok = type.IsEnum
            ? type.EnumNames.Any(name => string.Equals(name, value, StringComparison.Ordinal))
            : IsScalarValueAssignable(type, value);

        if (!ok) {
            AddDiagnostic(element, "TYPE005", DiagnosticSeverity.Error,
                $"Value '{value}' is not assignable to {type.Name}.", ValidationStage.Type, defType, defName);
        }
        });
    }

    private static bool HasElementChildren(XElement element) => element.Elements().Any();

    private bool CanValidateAsLeafText(XElement element, CatalogType type) {
        return !HasElementChildren(element)
               && !string.IsNullOrWhiteSpace(element.Value)
               && catalog.SupportsLeafText(type);
    }

    private bool IsScalarValueAssignable(CatalogType type, string value) {
        if (catalog.IsAssignableTo(type, "RimWorld.QuestGen.ISlateRef")) {
            return true;
        }

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
            "System.Type" => value is "null" or "Null" || catalog.FindType(value) is not null,
            "System.Action" => LooksLikeActionReference(value),
            "UnityEngine.Vector2" => TryParseFloatComponents(value, 1, 2),
            "UnityEngine.Vector3" => TryParseFloatComponents(value, 3, 3),
            "UnityEngine.Vector4" => TryParseFloatComponents(value, 1, 4),
            "UnityEngine.Quaternion" => TryParseFloatComponents(value, 1, 4),
            "UnityEngine.Rect" => TryParseFloatComponents(value, 4, 4),
            "UnityEngine.Color" => TryParseFloatComponents(TrimColorValue(value), 3, 4),
            "Verse.ColorInt" => TryParseIntComponents(TrimColorValue(value), 3, 4),
            "Verse.IntVec2" => TryParseIntComponents(value, 2, 2),
            "Verse.IntVec3" => TryParseIntComponents(value, 3, 3),
            "Verse.Rot4" => int.TryParse(value, out _) || value is "North" or "East" or "South" or "West",
            "Verse.CellRect" => TryParseIntComponents(value, 4, 4),
            "Verse.CurvePoint" => TryParseFloatComponents(value, 2, 2),
            "Verse.NameTriple" => !string.IsNullOrWhiteSpace(value),
            "Verse.FloatRange" => TryParseFloatRange(value),
            "Verse.IntRange" => TryParseIntRange(value),
            "RimWorld.QualityRange" => TryParseQualityRange(value),
            "Verse.TaggedString" => true,
            "RimWorld.Planet.PlanetTile" => TryParsePlanetTile(value),
            "Steamworks.PublishedFileId_t" => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            _ when type.IsPrimitive => value.Length > 0,
            _ => false
        };
    }

    private static bool LooksLikeActionReference(string value) {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length is 2 or 3;
    }

    private static string TrimColorValue(string value) => value.TrimStart('(', 'R', 'G', 'B', 'A').TrimEnd(')');

    private static bool TryParseFloatComponents(string value, int minCount, int maxCount) {
        var parts = SplitComponents(value);
        if (parts.Length < minCount || parts.Length > maxCount) {
            return false;
        }

        return parts.All(static part => float.TryParse(part, NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out _));
    }

    private static bool TryParseIntComponents(string value, int minCount, int maxCount) {
        var parts = SplitComponents(value);
        if (parts.Length < minCount || parts.Length > maxCount) {
            return false;
        }

        return parts.All(static part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    private static string[] SplitComponents(string value) {
        return value.TrimStart('(')
            .TrimEnd(')')
            .Split(',', StringSplitOptions.TrimEntries);
    }

    private static bool TryParseFloatRange(string value) {
        var parts = value.Split('~', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace)) {
            return false;
        }

        return parts.All(static part => float.TryParse(part, NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out _));
    }

    private static bool TryParseIntRange(string value) {
        var parts = value.Split('~', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2) {
            return false;
        }

        if (parts.Length == 1) {
            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        return (string.IsNullOrEmpty(parts[0])
                || int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
               && (string.IsNullOrEmpty(parts[1])
                   || int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    private bool TryParseQualityRange(string value) {
        var parts = value.Split('~', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) {
            return false;
        }

        var qualityCategory = catalog.FindType("RimWorld.QualityCategory") ?? catalog.FindType("QualityCategory");
        if (qualityCategory is null || !qualityCategory.IsEnum) {
            return false;
        }

        return parts.All(part => qualityCategory.EnumNames.Any(name => string.Equals(name, part, StringComparison.Ordinal)));
    }

    private static bool TryParsePlanetTile(string value) {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2) {
            return false;
        }

        return parts.All(static part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    private CatalogType? ResolveClassOverride(XElement element, CatalogType declaredType, string defType, string? defName) {
        return MeasureValue("resolve_class_override", () => {
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
        });
    }

    private void CollectReference(XElement element, CatalogType targetType, string defType, string? defName) {
        Measure("collect_reference", () => {
        var value = (element.Value).Trim();
        if (string.IsNullOrWhiteSpace(value)) {
            AddDiagnostic(element, "XREF003", DiagnosticSeverity.Error,
                $"Empty Def reference for type {targetType.Name}.", ValidationStage.Xref, defType, defName);
            return;
        }

        _references.Add(new PendingReference(element.Annotation<SourceInfo>() ?? new SourceInfo(null, null, null, null),
            targetType, value, defType, defName));
        });
    }

    private T MeasureValue<T>(string name, Func<T> action) {
        if (profiler is null) {
            return action();
        }

        return profiler.MeasureValue(name, action);
    }

    private void Measure(string name, Action action) {
        if (profiler is null) {
            action();
            return;
        }

        profiler.Measure(name, action);
    }

    private void AddDiagnostic(XElement element, string code, DiagnosticSeverity severity, string message,
        ValidationStage stage, string? defType, string? defName) {
        var source = element.Annotation<SourceInfo>();
        diagnostics.Add(code, severity, message, stage, source?.File, source?.Line, source?.Column, source?.PackageId,
            defType, defName);
    }

    private static bool HasTrueAttribute(XElement element, string attributeName) {
        var value = element.Attribute(attributeName)?.Value;
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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

internal sealed class StepProfiler {
    private readonly Dictionary<string, (long Ticks, int Count)> _stats = new(StringComparer.Ordinal);

    public T MeasureValue<T>(string name, Func<T> action) {
        var stopwatch = Stopwatch.StartNew();
        try {
            return action();
        } finally {
            stopwatch.Stop();
            Add(name, stopwatch.ElapsedTicks);
        }
    }

    public void Measure(string name, Action action) {
        var stopwatch = Stopwatch.StartNew();
        try {
            action();
        } finally {
            stopwatch.Stop();
            Add(name, stopwatch.ElapsedTicks);
        }
    }

    public IReadOnlyList<ValidationTiming> Export(string prefix) {
        return _stats
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
.Select(pair => new ValidationTiming($"{prefix}.{pair.Key}", Stopwatch.GetElapsedTime(0, pair.Value.Ticks), pair.Value.Count))
            .ToList();
    }

    private void Add(string name, long ticks) {
        if (_stats.TryGetValue(name, out var existing)) {
            _stats[name] = (existing.Ticks + ticks, existing.Count + 1);
            return;
        }

        _stats[name] = (ticks, 1);
    }
}

internal sealed record SourceInfo(string? File, int? Line, int? Column, string? PackageId);
