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
