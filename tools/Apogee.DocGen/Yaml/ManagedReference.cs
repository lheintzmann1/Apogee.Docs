namespace Apogee.DocGen.Yaml;

/// <summary>
/// The subset of DocFX's ManagedReference schema that Apogee's C++ and Lua APIs need.
///
/// Both generators target this one model so the three API surfaces (C#, C++, Lua) render through
/// the same DocFX templates and end up looking like one reference rather than three bolted
/// together. DocFX's own `metadata` command produces the C# half of it from Roslyn.
/// </summary>
public sealed class ApiItem
{
    public required string Uid { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string NameWithType { get; init; }
    public required string FullName { get; init; }

    /// <summary>Namespace, Class, Struct, Interface, Enum, Method, Constructor, Operator, Field, Property.</summary>
    public required string Type { get; init; }

    /// <summary>DocFX's language tag, e.g. "cplusplus" or "lua". Drives syntax highlighting.</summary>
    public required string Language { get; init; }

    public string? CommentId { get; init; }
    public string? Parent { get; set; }
    public string? Namespace { get; init; }
    public string? Summary { get; set; }
    public string? Remarks { get; set; }
    public string? Example { get; set; }
    public string? SyntaxContent { get; set; }
    public string? ReturnType { get; set; }
    public string? ReturnDescription { get; set; }
    public string? Source { get; set; }
    public int SourceLine { get; set; }
    public bool IsDeprecated { get; set; }
    public string? DeprecationMessage { get; set; }

    public List<string> Children { get; } = [];
    public List<string> Inheritance { get; } = [];
    public List<string> DerivedClasses { get; } = [];
    public List<ApiParameter> Parameters { get; } = [];
    public List<string> SeeAlso { get; } = [];
    public List<string> Overloads { get; } = [];
}

public sealed class ApiParameter
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; set; }
    public string? DefaultValue { get; init; }
    public bool Optional { get; init; }
}

/// <summary>A bare entry in the trailing `references:` block, used to give a uid a display name.</summary>
public sealed class ApiReference
{
    public required string Uid { get; init; }
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public string? CommentId { get; init; }
    public bool IsExternal { get; init; }

    /// <summary>
    /// Member kind, required when the reference stands in for a child that lives on another page.
    /// The DocFX template groups a type's children by this and fails outright when it is absent.
    /// </summary>
    public string? Type { get; init; }

    public string? Parent { get; init; }
    public string? Summary { get; init; }
}

public static class ManagedReferenceWriter
{
    /// <summary>
    /// Writes one ManagedReference page: a type followed by its members. DocFX splits these into
    /// per-member anchors itself, so members must be emitted alongside their parent, not alone.
    /// </summary>
    public static string Write(IReadOnlyList<ApiItem> items, IReadOnlyCollection<ApiReference> references)
    {
        var w = new YamlWriter();
        w.Raw("### YamlMime:ManagedReference");
        using (w.Section("items"))
        {
            foreach (var item in items)
                WriteItem(w, item);
        }

        if (references.Count > 0)
        {
            using (w.Section("references"))
            {
                foreach (var r in references.OrderBy(r => r.Uid, StringComparer.Ordinal))
                {
                    using (w.Item())
                    {
                        w.Key("uid", r.Uid);
                        w.Key("commentId", r.CommentId);
                        w.Key("parent", r.Parent);
                        w.Key("name", r.Name);
                        w.Key("nameWithType", r.Name);
                        w.Key("fullName", r.FullName ?? r.Name);
                        w.Key("type", r.Type);
                        w.Key("summary", r.Summary);
                        if (r.IsExternal)
                            w.KeyLiteral("isExternal", "true");
                    }
                }
            }
        }

        return w.ToString();
    }

    private static void WriteItem(YamlWriter w, ApiItem item)
    {
        using (w.Item())
        {
            w.Key("uid", item.Uid);
            w.Key("commentId", item.CommentId);
            w.Key("id", item.Id);
            w.Key("parent", item.Parent);
            w.List("children", item.Children);
            w.List("langs", [item.Language]);
            w.Key("name", item.Name);
            w.Key("nameWithType", item.NameWithType);
            w.Key("fullName", item.FullName);
            w.Key("type", item.Type);

            if (item.Source is not null)
            {
                using (w.Section("source"))
                {
                    w.Key("id", item.Id);
                    w.Key("path", item.Source);
                    if (item.SourceLine > 0)
                        w.KeyLiteral("startLine", item.SourceLine.ToString());
                }
            }

            w.Key("namespace", item.Namespace);
            w.Key("summary", item.Summary);

            // DocFX's `seealso` field holds structured link objects (linkType/linkId/commentId).
            // What Doxygen and the binding comments give us is free text, which is not the same
            // thing and fails deserialization, so it is rendered as prose in the remarks instead.
            var remarks = item.Remarks;
            if (item.SeeAlso.Count > 0)
            {
                var seeAlso = "**See also:** " + string.Join(", ", item.SeeAlso);
                remarks = string.IsNullOrEmpty(remarks) ? seeAlso : remarks + "\n\n" + seeAlso;
            }
            w.Key("remarks", remarks);
            w.Key("example", item.Example);

            if (item.SyntaxContent is not null || item.Parameters.Count > 0 || item.ReturnType is not null)
            {
                using (w.Section("syntax"))
                {
                    w.Key("content", item.SyntaxContent);
                    if (item.Parameters.Count > 0)
                    {
                        using (w.Section("parameters"))
                        {
                            foreach (var p in item.Parameters)
                            {
                                using (w.Item())
                                {
                                    w.Key("id", p.Name);
                                    w.Key("type", p.Type);
                                    w.Key("description", Describe(p));
                                }
                            }
                        }
                    }
                    if (item.ReturnType is not null)
                    {
                        using (w.Section("return"))
                        {
                            w.Key("type", item.ReturnType);
                            w.Key("description", item.ReturnDescription);
                        }
                    }
                }
            }

            w.List("inheritance", item.Inheritance);
            w.List("derivedClasses", item.DerivedClasses);

            if (item.IsDeprecated)
            {
                // DocFX renders this as the standard "Deprecated" banner above the member.
                using (w.Section("attributes"))
                {
                    using (w.Item())
                    {
                        w.Key("type", "System.ObsoleteAttribute");
                        w.Key("ctor", "System.ObsoleteAttribute.#ctor(System.String)");
                        w.List("arguments", [item.DeprecationMessage ?? "Deprecated."]);
                    }
                }
            }
        }
    }

    private static string? Describe(ApiParameter p)
    {
        var description = p.Description;
        if (p.DefaultValue is not null)
        {
            var note = $"Defaults to `{p.DefaultValue}`.";
            description = string.IsNullOrEmpty(description) ? note : description.TrimEnd() + " " + note;
        }
        else if (p.Optional && string.IsNullOrEmpty(description))
        {
            description = "Optional.";
        }
        return description;
    }
}
