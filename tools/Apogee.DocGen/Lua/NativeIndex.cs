namespace Apogee.DocGen.Lua;

using System.Xml.Linq;
using Apogee.DocGen.Cpp;

public sealed class NativeMember
{
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required bool IsFunction { get; init; }
    public string? ReturnType { get; init; }
    public List<(string Name, string Type)> Parameters { get; init; } = [];
    public string? Summary { get; init; }
    public Dictionary<string, string> ParameterDocs { get; init; } = new(StringComparer.Ordinal);
    public string? Returns { get; init; }
}

/// <summary>
/// An index of the engine's C++ members, built from the same Doxygen XML the C++ API pages use.
///
/// Most Lua bindings are thin forwards — <c>"GetName", &amp;Actor::GetName</c> — which on their own
/// carry no types and no prose. Resolving them against the native declaration lets a binding
/// inherit the signature and the <c>&lt;summary&gt;</c> already written in the header, so the Lua
/// reference stays accurate without duplicating documentation into the binding files.
/// </summary>
public sealed class NativeIndex
{
    private readonly Dictionary<string, NativeMember> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _typedefs = new(StringComparer.Ordinal);

    public static NativeIndex Empty { get; } = new();

    public static NativeIndex Load(string xmlDirectory)
    {
        var index = new NativeIndex();
        var indexPath = Path.Combine(xmlDirectory, "index.xml");
        if (!File.Exists(indexPath))
            return index;

        foreach (var compound in XDocument.Load(indexPath).Root!.Elements("compound"))
        {
            var kind = (string?)compound.Attribute("kind");
            if (kind == "file")
            {
                index.LoadTypedefs(xmlDirectory, (string?)compound.Attribute("refid"));
                continue;
            }
            if (kind is not ("class" or "struct" or "namespace" or "union" or "interface"))
                continue;
            var refId = (string?)compound.Attribute("refid");
            if (refId is null)
                continue;
            var file = Path.Combine(xmlDirectory, refId + ".xml");
            if (!File.Exists(file))
                continue;

            var definition = XDocument.Load(file).Root!.Element("compounddef");
            var typeName = definition?.Element("compoundname")?.Value.Trim();
            if (definition is null || string.IsNullOrEmpty(typeName))
                continue;

            foreach (var member in definition.Descendants("memberdef"))
            {
                var memberKind = (string?)member.Attribute("kind");
                if (memberKind is not ("function" or "variable"))
                    continue;
                var name = member.Element("name")?.Value.Trim();
                if (string.IsNullOrEmpty(name))
                    continue;

                var description = DoxygenDescription.Parse(
                    member.Element("briefdescription"), member.Element("detaileddescription"));

                var parameters = member.Elements("param")
                    .Select(p => (
                        Name: p.Element("declname")?.Value.Trim() ?? p.Element("defname")?.Value.Trim() ?? "arg",
                        Type: Clean(p.Element("type")?.Value)))
                    .ToList();

                var entry = new NativeMember
                {
                    Type = typeName,
                    Name = name,
                    IsFunction = memberKind == "function",
                    ReturnType = Clean(member.Element("type")?.Value),
                    Parameters = parameters,
                    Summary = description.Body,
                    Returns = description.Returns,
                };
                foreach (var (key, value) in description.Parameters)
                    entry.ParameterDocs[key] = value;

                // Overloads collapse onto the first declaration; the Lua binding picks one anyway,
                // and the richer of the two is almost always the one declared first.
                index._members.TryAdd($"{typeName}::{name}", entry);
            }
        }

        return index;
    }

    /// <summary>
    /// File-scope typedefs. The engine's public vocabulary is largely aliases — <c>Vector3</c> is
    /// <c>Vector3Base&lt;Real&gt;</c> and <c>Real</c> is <c>float</c> unless the build asked for
    /// large worlds — and a binding declared in terms of one has to be resolved through it before
    /// the Lua type behind it can be recognised.
    /// </summary>
    private void LoadTypedefs(string xmlDirectory, string? refId)
    {
        if (refId is null)
            return;
        var file = Path.Combine(xmlDirectory, refId + ".xml");
        if (!File.Exists(file))
            return;

        var definition = XDocument.Load(file).Root!.Element("compounddef");
        if (definition is null)
            return;

        foreach (var member in definition.Descendants("memberdef"))
        {
            if ((string?)member.Attribute("kind") != "typedef")
                continue;
            var name = member.Element("name")?.Value.Trim();
            var type = member.Element("type");
            if (string.IsNullOrEmpty(name) || type is null)
                continue;
            // The target is mixed content: `Vector3Base< Real >` is text around a <ref> element.
            _typedefs.TryAdd(name, Clean(string.Concat(type.Nodes().Select(NodeText))));
        }
    }

    private static string NodeText(XNode node) =>
        node is XElement element ? element.Value : node is XText text ? text.Value : string.Empty;

    /// <summary>
    /// Follows a chain of typedefs to what it finally names, leaving anything that is not one
    /// untouched: <c>Vector3</c> -> <c>Vector3Base&lt;float&gt;</c>, <c>Real</c> -> <c>float</c>.
    /// </summary>
    public string ResolveAliases(string type)
    {
        if (_typedefs.Count == 0)
            return type;

        var text = type;
        // Aliases nest one level deep here (Vector3 -> Vector3Base<Real> -> Vector3Base<float>);
        // the bound is what stops a typedef that names itself from looping.
        for (var pass = 0; pass < 4; pass++)
        {
            var before = text;
            text = TypeTokenPattern.Replace(text, match =>
                _typedefs.TryGetValue(match.Value, out var target) && target != match.Value
                    ? target
                    : match.Value);
            if (text == before)
                break;
        }
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s*([<>])\s*", "$1").Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex TypeTokenPattern =
        new(@"\b[A-Za-z_]\w*\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    public NativeMember? Lookup(string type, string name) =>
        _members.GetValueOrDefault($"{type}::{name}");

    /// <summary>Resolves a member of <paramref name="type"/> or of any type it is aliased to.</summary>
    public NativeMember? Lookup(string qualified)
    {
        if (_members.TryGetValue(qualified, out var direct))
            return direct;
        var separator = qualified.LastIndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            return null;
        var type = qualified[..separator];
        var name = qualified[(separator + 2)..];
        // Template instantiations arrive as VecT/Vector3Base<float>; try the bare template name.
        var angle = type.IndexOf('<');
        if (angle > 0)
            return _members.GetValueOrDefault($"{type[..angle]}::{name}");
        return null;
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
}
