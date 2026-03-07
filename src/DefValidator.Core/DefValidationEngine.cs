using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace DefValidator.Core;

public sealed class DefValidationEngine
{
    public Task<ValidationResult> ValidateAsync(ValidationOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new DiagnosticBag();
        var context = ModContextBuilder.Build(options, diagnostics);
        if (context is null)
        {
            return Task.FromResult(diagnostics.ToResult());
        }

        var catalog = AssemblyCatalog.Load(context, diagnostics);
        var aggregate = XmlPipeline.BuildAggregate(context, options.ApplyPatches, diagnostics);
        if (aggregate is null)
        {
            return Task.FromResult(diagnostics.ToResult());
        }

        var validator = new SemanticValidator(catalog, diagnostics);
        validator.Validate(aggregate);
        return Task.FromResult(diagnostics.ToResult());
    }
}

internal static class XmlPipeline
{
    public static XDocument? BuildAggregate(ModContext context, bool applyPatches, DiagnosticBag diagnostics)
    {
        var defDocuments = new List<(ModInfo Mod, string FilePath, XDocument Document)>();
        var patchDocuments = new List<(ModInfo Mod, string FilePath, XDocument Document)>();

        foreach (var mod in context.ModsInLoadOrder)
        {
            foreach (var folder in mod.LoadFolders)
            {
                var root = Path.GetFullPath(Path.Combine(mod.RootPath, folder));
                CollectXmlDocuments(mod, root, "Defs", defDocuments, diagnostics, context.ActivePackageIds);
                CollectXmlDocuments(mod, root, "Patches", patchDocuments, diagnostics, context.ActivePackageIds);
            }
        }

        var aggregate = new XDocument(new XElement("Defs"));
        foreach (var (_, _, document) in defDocuments)
        {
            if (document.Root is null)
            {
                continue;
            }

            foreach (var child in document.Root.Elements())
            {
                aggregate.Root!.Add(CloneElement(child));
            }
        }

        if (applyPatches)
        {
            foreach (var (mod, filePath, document) in patchDocuments)
            {
                if (document.Root is null)
                {
                    continue;
                }

                foreach (var operation in document.Root.Elements().Where(static element => element.Name.LocalName is "Operation" or "li"))
                {
                    PatchEngine.ApplyOperation(aggregate, operation, mod, context.ActivePackageIds, diagnostics, filePath);
                }
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
        IReadOnlySet<string> activePackageIds)
    {
        var directory = Path.Combine(root, subfolder);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                if (document.Root is not null)
                {
                    Annotate(document.Root, filePath, mod.PackageId);
                    ApplyMayRequire(document.Root, activePackageIds);
                }

                destination.Add((mod, filePath, document));
            }
            catch (XmlException ex)
            {
                diagnostics.Add("XML001", DiagnosticSeverity.Error, ex.Message, ValidationStage.XmlLoad, filePath, ex.LineNumber, ex.LinePosition, mod.PackageId);
            }
        }
    }

    private static void Annotate(XElement root, string filePath, string packageId)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var lineInfo = (IXmlLineInfo)element;
            element.AddAnnotation(new SourceInfo(filePath, lineInfo.HasLineInfo() ? lineInfo.LineNumber : null, lineInfo.HasLineInfo() ? lineInfo.LinePosition : null, packageId));
        }
    }

    private static void ApplyMayRequire(XElement root, IReadOnlySet<string> activePackageIds)
    {
        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            var mayRequire = element.Attribute("MayRequire")?.Value;
            var mayRequireAnyOf = element.Attribute("MayRequireAnyOf")?.Value;
            if (IsAllowed(mayRequire, mayRequireAnyOf, activePackageIds))
            {
                continue;
            }

            element.Remove();
        }
    }

    private static bool IsAllowed(string? mayRequire, string? mayRequireAnyOf, IReadOnlySet<string> activePackageIds)
    {
        if (!string.IsNullOrWhiteSpace(mayRequire))
        {
            var allPresent = mayRequire.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(activePackageIds.Contains);
            if (!allPresent)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(mayRequireAnyOf))
        {
            var anyPresent = mayRequireAnyOf.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(activePackageIds.Contains);
            if (!anyPresent)
            {
                return false;
            }
        }

        return true;
    }

    internal static XElement CloneElement(XElement element)
    {
        var clone = new XElement(element.Name,
            element.Attributes().Select(static attribute => new XAttribute(attribute)),
            element.Nodes().Select(CloneNode).Where(static node => node is not null)!);

        if (element.Annotation<SourceInfo>() is { } source)
        {
            clone.AddAnnotation(source);
        }

        return clone;
    }

    internal static XNode? CloneNode(XNode node) => node switch
    {
        XElement child => CloneElement(child),
        XCData cdata => new XCData(cdata.Value),
        XText text => new XText(text.Value),
        XComment comment => new XComment(comment.Value),
        _ => null
    };
}

internal static class PatchEngine
{
    public static bool ApplyOperation(XDocument aggregate, XElement operationElement, ModInfo mod, IReadOnlySet<string> activePackageIds, DiagnosticBag diagnostics, string filePath)
    {
        var className = operationElement.Attribute("Class")?.Value?.Trim();
        var source = operationElement.Annotation<SourceInfo>();
        var simpleName = className?.Split('.').LastOrDefault();
        if (string.IsNullOrWhiteSpace(simpleName))
        {
            diagnostics.Add("PATCH001", DiagnosticSeverity.Error, "Patch operation is missing Class.", ValidationStage.Patch, filePath, source?.Line, source?.Column, mod.PackageId);
            return false;
        }

        bool success = simpleName switch
        {
            "PatchOperationAdd" => ApplyAddLike(aggregate, operationElement, prepend: ReadOrder(operationElement) == "Prepend"),
            "PatchOperationReplace" => ApplyReplace(aggregate, operationElement),
            "PatchOperationRemove" => ApplyRemove(aggregate, operationElement),
            "PatchOperationInsert" => ApplyInsert(aggregate, operationElement, prepend: ReadOrder(operationElement) != "Append"),
            "PatchOperationSequence" => ApplySequence(aggregate, operationElement, mod, activePackageIds, diagnostics, filePath),
            "PatchOperationConditional" => ApplyConditional(aggregate, operationElement, mod, activePackageIds, diagnostics, filePath),
            "PatchOperationFindMod" => ApplyFindMod(aggregate, operationElement, mod, activePackageIds, diagnostics, filePath),
            "PatchOperationSetName" => ApplySetName(aggregate, operationElement),
            "PatchOperationAttributeAdd" => ApplyAttribute(aggregate, operationElement, AttributeMode.Add),
            "PatchOperationAttributeRemove" => ApplyAttribute(aggregate, operationElement, AttributeMode.Remove),
            "PatchOperationAttributeSet" => ApplyAttribute(aggregate, operationElement, AttributeMode.Set),
            "PatchOperationAddModExtension" => ApplyAddModExtension(aggregate, operationElement),
            _ => Unsupported(simpleName, diagnostics, source, mod.PackageId, filePath)
        };

        if (!success)
        {
            diagnostics.Add("PATCH002", DiagnosticSeverity.Warning, $"Patch operation {simpleName} did not match any nodes.", ValidationStage.Patch, filePath, source?.Line, source?.Column, mod.PackageId);
        }

        return success;
    }

    private static bool Unsupported(string simpleName, DiagnosticBag diagnostics, SourceInfo? source, string packageId, string filePath)
    {
        diagnostics.Add("PATCH001", DiagnosticSeverity.Warning, $"Unsupported patch operation: {simpleName}", ValidationStage.Patch, filePath, source?.Line, source?.Column, packageId);
        return false;
    }

    private static bool ApplyAddLike(XDocument aggregate, XElement operationElement, bool prepend)
    {
        var targets = SelectTargets(aggregate, operationElement).ToList();
        var value = operationElement.Element("value");
        if (targets.Count == 0 || value is null)
        {
            return false;
        }

        foreach (var target in targets)
        {
            var children = value.Elements().Select(XmlPipeline.CloneElement).ToList();
            if (prepend)
            {
                target.AddFirst(children.Reverse<XElement>());
            }
            else
            {
                target.Add(children);
            }
        }

        return true;
    }

    private static bool ApplyInsert(XDocument aggregate, XElement operationElement, bool prepend)
    {
        var targets = SelectTargets(aggregate, operationElement).ToList();
        var value = operationElement.Element("value");
        if (targets.Count == 0 || value is null)
        {
            return false;
        }

        foreach (var target in targets)
        {
            var parent = target.Parent;
            if (parent is null)
            {
                continue;
            }

            var items = value.Elements().Select(XmlPipeline.CloneElement).ToList();
            if (prepend)
            {
                foreach (var item in items.Reverse<XElement>())
                {
                    target.AddBeforeSelf(item);
                }
            }
            else
            {
                foreach (var item in items)
                {
                    target.AddAfterSelf(item);
                }
            }
        }

        return true;
    }

    private static bool ApplyReplace(XDocument aggregate, XElement operationElement)
    {
        var targets = SelectTargets(aggregate, operationElement).ToList();
        var value = operationElement.Element("value");
        if (targets.Count == 0 || value is null)
        {
            return false;
        }

        foreach (var target in targets)
        {
            var replacements = value.Elements().Select(XmlPipeline.CloneElement).ToList();
            target.ReplaceWith(replacements);
        }

        return true;
    }

    private static bool ApplyRemove(XDocument aggregate, XElement operationElement)
    {
        var targets = SelectTargets(aggregate, operationElement).ToList();
        if (targets.Count == 0)
        {
            return false;
        }

        targets.Remove();
        return true;
    }

    private static bool ApplySequence(XDocument aggregate, XElement operationElement, ModInfo mod, IReadOnlySet<string> activePackageIds, DiagnosticBag diagnostics, string filePath)
    {
        var operations = operationElement.Element("operations")?.Elements().ToList() ?? [];
        if (operations.Count == 0)
        {
            return false;
        }

        foreach (var operation in operations)
        {
            if (!ApplyOperation(aggregate, operation, mod, activePackageIds, diagnostics, filePath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ApplyConditional(XDocument aggregate, XElement operationElement, ModInfo mod, IReadOnlySet<string> activePackageIds, DiagnosticBag diagnostics, string filePath)
    {
        var hasMatch = SelectTargets(aggregate, operationElement).Any();
        var branch = hasMatch ? operationElement.Element("match") : operationElement.Element("nomatch");
        var nested = branch?.Elements().FirstOrDefault();
        return nested is null || ApplyOperation(aggregate, nested, mod, activePackageIds, diagnostics, filePath);
    }

    private static bool ApplyFindMod(XDocument aggregate, XElement operationElement, ModInfo mod, IReadOnlySet<string> activePackageIds, DiagnosticBag diagnostics, string filePath)
    {
        var hasMatch = operationElement.Element("mods")?.Elements().Any(li => activePackageIds.Contains(li.Value.Trim())) == true;
        var branch = hasMatch ? operationElement.Element("match") : operationElement.Element("nomatch");
        var nested = branch?.Elements().FirstOrDefault();
        return nested is null || ApplyOperation(aggregate, nested, mod, activePackageIds, diagnostics, filePath);
    }

    private static bool ApplySetName(XDocument aggregate, XElement operationElement)
    {
        var name = operationElement.Element("name")?.Value.Trim();
        var targets = SelectTargets(aggregate, operationElement).ToList();
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var target in targets)
        {
            target.Name = name;
        }

        return true;
    }

    private static bool ApplyAddModExtension(XDocument aggregate, XElement operationElement)
    {
        var targets = SelectTargets(aggregate, operationElement).ToList();
        var value = operationElement.Element("value");
        if (targets.Count == 0 || value is null)
        {
            return false;
        }

        foreach (var target in targets)
        {
            var extensionContainer = target.Element("modExtensions");
            if (extensionContainer is null)
            {
                extensionContainer = new XElement("modExtensions");
                if (target.Annotation<SourceInfo>() is { } source)
                {
                    extensionContainer.AddAnnotation(source);
                }

                target.Add(extensionContainer);
            }

            extensionContainer.Add(value.Elements().Select(XmlPipeline.CloneElement));
        }

        return true;
    }

    private static bool ApplyAttribute(XDocument aggregate, XElement operationElement, AttributeMode mode)
    {
        var attribute = operationElement.Element("attribute")?.Value.Trim();
        var value = operationElement.Element("value")?.Value ?? string.Empty;
        var targets = SelectTargets(aggregate, operationElement).ToList();
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(attribute))
        {
            return false;
        }

        foreach (var target in targets)
        {
            var existing = target.Attribute(attribute);
            switch (mode)
            {
                case AttributeMode.Add when existing is null:
                    target.SetAttributeValue(attribute, value);
                    break;
                case AttributeMode.Remove when existing is not null:
                    existing.Remove();
                    break;
                case AttributeMode.Set:
                    target.SetAttributeValue(attribute, value);
                    break;
            }
        }

        return true;
    }

    private static IEnumerable<XElement> SelectTargets(XDocument aggregate, XElement operationElement)
    {
        var xpath = operationElement.Element("xpath")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(xpath))
        {
            return [];
        }

        try
        {
            return aggregate.XPathSelectElements(xpath).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string ReadOrder(XElement operationElement) => operationElement.Element("order")?.Value.Trim() ?? "Append";

    private enum AttributeMode
    {
        Add,
        Remove,
        Set
    }
}

internal static class InheritanceResolver
{
    public static XDocument Resolve(XDocument aggregate, DiagnosticBag diagnostics)
    {
        var root = aggregate.Root ?? new XElement("Defs");
        var nodes = root.Elements().Select((element, index) => new NodeEntry(element, index)).ToList();
        var named = nodes
            .Where(static node => !string.IsNullOrWhiteSpace(node.Name))
            .GroupBy(static node => node.Name!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static node => node.LoadOrder).ToList(), StringComparer.Ordinal);

        var resolved = new Dictionary<XElement, XElement>();
        var visiting = new HashSet<XElement>();

        XElement ResolveNode(NodeEntry node)
        {
            if (resolved.TryGetValue(node.Element, out var existing))
            {
                return existing;
            }

            if (!visiting.Add(node.Element))
            {
                var source = node.Element.Annotation<SourceInfo>();
                diagnostics.Add("INHERIT002", DiagnosticSeverity.Error, "Cyclic inheritance hierarchy detected.", ValidationStage.Inheritance, source?.File, source?.Line, source?.Column, source?.PackageId);
                return XmlPipeline.CloneElement(node.Element);
            }

            XElement result;
            if (string.IsNullOrWhiteSpace(node.ParentName))
            {
                result = XmlPipeline.CloneElement(node.Element);
            }
            else if (!named.TryGetValue(node.ParentName, out var candidates))
            {
                var source = node.Element.Annotation<SourceInfo>();
                diagnostics.Add("INHERIT001", DiagnosticSeverity.Error, $"Could not find ParentName '{node.ParentName}'.", ValidationStage.Inheritance, source?.File, source?.Line, source?.Column, source?.PackageId, node.Element.Name.LocalName);
                result = XmlPipeline.CloneElement(node.Element);
            }
            else
            {
                var parent = candidates.Where(candidate => candidate.LoadOrder <= node.LoadOrder).OrderByDescending(static candidate => candidate.LoadOrder).FirstOrDefault();
                if (parent is null)
                {
                    var source = node.Element.Annotation<SourceInfo>();
                    diagnostics.Add("INHERIT001", DiagnosticSeverity.Error, $"Could not find ParentName '{node.ParentName}'.", ValidationStage.Inheritance, source?.File, source?.Line, source?.Column, source?.PackageId, node.Element.Name.LocalName);
                    result = XmlPipeline.CloneElement(node.Element);
                }
                else
                {
                    result = Merge(ResolveNode(parent), node.Element, diagnostics);
                }
            }

            visiting.Remove(node.Element);
            resolved[node.Element] = result;
            return result;
        }

        var newRoot = new XElement("Defs", nodes.Select(ResolveNode));
        return new XDocument(newRoot);
    }

    private static XElement Merge(XElement parent, XElement child, DiagnosticBag diagnostics)
    {
        CheckDuplicateChildNames(child, diagnostics);
        var clone = XmlPipeline.CloneElement(parent);
        MergeInto(clone, child);
        return clone;
    }

    private static void MergeInto(XElement current, XElement child)
    {
        var inheritAttribute = child.Attribute("Inherit")?.Value;
        if (string.Equals(inheritAttribute, "false", StringComparison.OrdinalIgnoreCase))
        {
            current.ReplaceAttributes(child.Attributes().Where(static attribute => attribute.Name.LocalName != "Inherit").Select(static attribute => new XAttribute(attribute)));
            current.ReplaceNodes(child.Nodes().Select(XmlPipeline.CloneNode).Where(static node => node is not null)!);
            CopySource(current, child);
            return;
        }

        current.ReplaceAttributes(child.Attributes().Select(static attribute => new XAttribute(attribute)));
        CopySource(current, child);

        var textNode = child.Nodes().OfType<XText>().FirstOrDefault();
        if (textNode is not null)
        {
            current.Value = textNode.Value;
            return;
        }

        var elementChildren = child.Elements().ToList();
        if (elementChildren.Count == 0)
        {
            return;
        }

        foreach (var childElement in elementChildren)
        {
            if (childElement.Name.LocalName == "li")
            {
                current.Add(XmlPipeline.CloneElement(childElement));
                continue;
            }

            var existing = current.Elements(childElement.Name).FirstOrDefault();
            if (existing is null)
            {
                current.Add(XmlPipeline.CloneElement(childElement));
                continue;
            }

            MergeInto(existing, childElement);
        }
    }

    private static void CheckDuplicateChildNames(XElement element, DiagnosticBag diagnostics)
    {
        var duplicates = element.Elements()
            .Where(static child => child.Name.LocalName != "li")
            .GroupBy(static child => child.Name.LocalName, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            var source = duplicate.First().Annotation<SourceInfo>();
            diagnostics.Add("INHERIT003", DiagnosticSeverity.Error, $"Duplicate XML node name '{duplicate.Key}' in inherited block.", ValidationStage.Inheritance, source?.File, source?.Line, source?.Column, source?.PackageId, element.Name.LocalName);
        }
    }

    private static void CopySource(XElement destination, XElement sourceElement)
    {
        if (sourceElement.Annotation<SourceInfo>() is { } source)
        {
            destination.RemoveAnnotations<SourceInfo>();
            destination.AddAnnotation(source);
        }
    }

    private sealed record NodeEntry(XElement Element, int LoadOrder)
    {
        public string? Name => Element.Attribute("Name")?.Value;

        public string? ParentName => Element.Attribute("ParentName")?.Value;
    }
}

internal sealed class SemanticValidator(AssemblyCatalog catalog, DiagnosticBag diagnostics)
{
    private static readonly HashSet<string> IgnoredAttributes = ["Class", "Name", "ParentName", "MayRequire", "MayRequireAnyOf", "Inherit"];
    private static readonly System.Text.RegularExpressions.Regex DefNamePattern = new("^[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly List<PendingReference> _references = [];
    private readonly List<ResolvedDef> _defs = [];

    public void Validate(XDocument aggregate)
    {
        foreach (var element in aggregate.Root?.Elements() ?? [])
        {
            ValidateRootDef(element);
        }

        var duplicates = _defs
            .Where(static def => !string.IsNullOrWhiteSpace(def.DefName))
            .GroupBy(static def => $"{def.Type.FullName ?? def.Type.Name}::{def.DefName}", StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            foreach (var def in duplicate)
            {
                AddDiagnostic(def.Element, "XREF001", DiagnosticSeverity.Error, $"Duplicate defName '{def.DefName}' for type {def.Type.Name}.", ValidationStage.Xref, def.Type.Name, def.DefName);
            }
        }

        foreach (var reference in _references)
        {
            var matched = _defs.Any(def => AssemblyCatalog.IsAssignableTo(def.Type, reference.TargetType) && string.Equals(def.DefName, reference.Value, StringComparison.Ordinal));
            if (!matched)
            {
                diagnostics.Add("XREF002", DiagnosticSeverity.Error, $"Could not resolve Def reference '{reference.Value}' for expected type {reference.TargetType.Name}.", ValidationStage.Xref, reference.Source.File, reference.Source.Line, reference.Source.Column, reference.Source.PackageId, reference.OwnerDefType, reference.OwnerDefName);
            }
        }
    }

    private void ValidateRootDef(XElement element)
    {
        var typeName = element.Attribute("Class")?.Value ?? element.Name.LocalName;
        var type = catalog.FindType(typeName);
        if (type is null)
        {
            AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Unknown Def class '{typeName}'.", ValidationStage.Type, element.Name.LocalName, GetDefName(element));
            return;
        }

        if (!catalog.IsDefType(type))
        {
            AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Type '{typeName}' is not a Verse.Def.", ValidationStage.Type, element.Name.LocalName, GetDefName(element));
            return;
        }

        var defName = GetDefName(element);
        if (string.IsNullOrWhiteSpace(defName))
        {
            AddDiagnostic(element, "RULE001", DiagnosticSeverity.Error, "Missing defName.", ValidationStage.Rule, element.Name.LocalName, defName);
        }
        else if (!DefNamePattern.IsMatch(defName))
        {
            AddDiagnostic(element, "RULE002", DiagnosticSeverity.Error, $"Invalid defName '{defName}'.", ValidationStage.Rule, element.Name.LocalName, defName);
        }

        ValidateObject(element, type, element.Name.LocalName, defName, isRoot: true);
        _defs.Add(new ResolvedDef(element, type, defName));
    }

    private void ValidateObject(XElement element, Type declaredType, string defType, string? defName, bool isRoot = false)
    {
        var actualType = ResolveClassOverride(element, declaredType, defType, defName) ?? declaredType;
        var members = catalog.GetMembers(actualType);
        var duplicates = element.Elements()
            .Where(static child => child.Name.LocalName != "li")
            .GroupBy(static child => child.Name.LocalName, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            if (members.TryGetValue(duplicate.Key, out var member) && AssemblyCatalog.IsListType(AssemblyCatalog.GetMemberType(member), out _))
            {
                continue;
            }

            var duplicateElement = duplicate.First();
            AddDiagnostic(duplicateElement, "TYPE003", DiagnosticSeverity.Error, $"Duplicate node '{duplicate.Key}'.", ValidationStage.Type, defType, defName);
        }

        foreach (var child in element.Elements())
        {
            if (!members.TryGetValue(child.Name.LocalName, out var member))
            {
                if (!isRoot || child.Name.LocalName != "defName")
                {
                    AddDiagnostic(child, "TYPE002", DiagnosticSeverity.Error, $"Unknown node '{child.Name.LocalName}' on type {actualType.Name}.", ValidationStage.Type, defType, defName);
                }

                continue;
            }

            var memberType = AssemblyCatalog.GetMemberType(member);
            ValidateValue(child, memberType, defType, defName);
        }
    }

    private void ValidateValue(XElement element, Type memberType, string defType, string? defName)
    {
        memberType = AssemblyCatalog.UnwrapNullable(memberType);
        if (AssemblyCatalog.IsListType(memberType, out var itemType))
        {
            foreach (var item in element.Elements())
            {
                var effectiveItemType = ResolveClassOverride(item, itemType, defType, defName) ?? itemType;
                if (catalog.IsDefType(effectiveItemType))
                {
                    CollectReference(item, effectiveItemType, defType, defName);
                    continue;
                }

                if (AssemblyCatalog.IsScalar(effectiveItemType))
                {
                    ValidateScalar(item, effectiveItemType, defType, defName);
                }
                else
                {
                    ValidateObject(item, effectiveItemType, defType, defName);
                }
            }

            return;
        }

        if (catalog.IsDefType(memberType))
        {
            CollectReference(element, memberType, defType, defName);
            return;
        }

        if (AssemblyCatalog.IsScalar(memberType))
        {
            ValidateScalar(element, memberType, defType, defName);
            return;
        }

        if ((memberType.IsAbstract || memberType.IsInterface) && element.Attribute("Class") is null)
        {
            AddDiagnostic(element, "TYPE004", DiagnosticSeverity.Error, $"Type {memberType.Name} requires a Class attribute.", ValidationStage.Type, defType, defName);
            return;
        }

        ValidateObject(element, memberType, defType, defName);
    }

    private void ValidateScalar(XElement element, Type type, string defType, string? defName)
    {
        var value = (element.Value ?? string.Empty).Trim();
        var ok = type.IsEnum
            ? Enum.GetNames(type).Any(name => string.Equals(name, value, StringComparison.Ordinal))
            : type == typeof(string)
              || type == typeof(int) && int.TryParse(value, out _)
              || type == typeof(float) && float.TryParse(value, out _)
              || type == typeof(double) && double.TryParse(value, out _)
              || type == typeof(decimal) && decimal.TryParse(value, out _)
              || type == typeof(bool) && bool.TryParse(value, out _)
              || type == typeof(Type) && catalog.FindType(value) is not null;

        if (!ok)
        {
            AddDiagnostic(element, "TYPE005", DiagnosticSeverity.Error, $"Value '{value}' is not assignable to {type.Name}.", ValidationStage.Type, defType, defName);
        }
    }

    private Type? ResolveClassOverride(XElement element, Type declaredType, string defType, string? defName)
    {
        var className = element.Attribute("Class")?.Value;
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        var resolvedType = catalog.FindType(className);
        if (resolvedType is null)
        {
            AddDiagnostic(element, "TYPE001", DiagnosticSeverity.Error, $"Unknown Class '{className}'.", ValidationStage.Type, defType, defName);
            return null;
        }

        if (!AssemblyCatalog.IsAssignableTo(resolvedType, declaredType))
        {
            AddDiagnostic(element, "TYPE006", DiagnosticSeverity.Error, $"Class '{className}' is not assignable to {declaredType.Name}.", ValidationStage.Type, defType, defName);
            return null;
        }

        return resolvedType;
    }

    private void CollectReference(XElement element, Type targetType, string defType, string? defName)
    {
        var value = (element.Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            AddDiagnostic(element, "XREF003", DiagnosticSeverity.Error, $"Empty Def reference for type {targetType.Name}.", ValidationStage.Xref, defType, defName);
            return;
        }

        _references.Add(new PendingReference(element.Annotation<SourceInfo>() ?? new SourceInfo(null, null, null, null), targetType, value, defType, defName));
    }

    private void AddDiagnostic(XElement element, string code, DiagnosticSeverity severity, string message, ValidationStage stage, string? defType, string? defName)
    {
        var source = element.Annotation<SourceInfo>();
        diagnostics.Add(code, severity, message, stage, source?.File, source?.Line, source?.Column, source?.PackageId, defType, defName);
    }

    private static string? GetDefName(XElement element) => element.Elements().FirstOrDefault(static child => child.Name.LocalName == "defName")?.Value.Trim();

    private sealed record PendingReference(SourceInfo Source, Type TargetType, string Value, string OwnerDefType, string? OwnerDefName);

    private sealed record ResolvedDef(XElement Element, Type Type, string? DefName);
}

internal sealed record SourceInfo(string? File, int? Line, int? Column, string? PackageId);
