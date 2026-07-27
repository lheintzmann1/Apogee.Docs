namespace Apogee.DocGen.Cpp;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Apogee.DocGen.Yaml;

public sealed class CppOptions
{
    /// <summary>Directory holding Doxygen's XML output (the one containing index.xml).</summary>
    public required string XmlDirectory { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>Engine repo root, used to turn absolute header paths into repo-relative ones.</summary>
    public required string EngineRoot { get; init; }

    /// <summary>Types matching any of these are dropped (internal plumbing, template helpers).</summary>
    public List<string> ExcludeTypes { get; init; } = [];

    /// <summary>Header paths (repo-relative, forward slashes) matching these are dropped.</summary>
    public List<string> ExcludePaths { get; init; } = [];

    public bool IncludeProtected { get; init; } = true;
}

/// <summary>
/// Converts Doxygen's XML into DocFX ManagedReference pages.
///
/// Why not code2yaml, the usual tool for this job: it is unmaintained, .NET Framework-era, and
/// its output shape predates the DocFX version we build with. Since the Lua side needs a bespoke
/// generator regardless, both languages share one emitter here and are guaranteed to render
/// identically.
/// </summary>
public sealed class CppGenerator(CppOptions options)
{
    private const string Language = "cplusplus";

    private readonly Dictionary<string, Compound> _compounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _derived = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownUids = new(StringComparer.Ordinal);

    private sealed class Compound
    {
        public required string RefId { get; init; }
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public required XElement Definition { get; init; }
        public string? HeaderPath { get; set; }
        public string? Module { get; set; }
    }

    public int Generate()
    {
        Directory.CreateDirectory(options.OutputDirectory);
        LoadCompounds();

        var emitted = new List<(string Uid, string Name, string Kind, string Module)>();
        foreach (var compound in _compounds.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var pages = BuildPage(compound);
            if (pages.Count == 0)
                continue;

            foreach (var (uid, content) in pages)
                File.WriteAllText(Path.Combine(options.OutputDirectory, FileNameFor(uid) + ".yml"), content);

            emitted.Add((Uid(compound.Name), compound.Name, compound.Kind, compound.Module ?? "Engine"));
        }

        WriteToc(emitted);
        return emitted.Count;
    }

    // ---- Discovery ---------------------------------------------------------

    private void LoadCompounds()
    {
        var indexPath = Path.Combine(options.XmlDirectory, "index.xml");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException($"Doxygen index not found at '{indexPath}'. Run doxygen first.");

        var index = XDocument.Load(indexPath);
        foreach (var entry in index.Root!.Elements("compound"))
        {
            var kind = (string?)entry.Attribute("kind");
            if (kind is not ("class" or "struct" or "namespace" or "interface" or "union"))
                continue;

            var refId = (string?)entry.Attribute("refid");
            if (refId is null)
                continue;

            var file = Path.Combine(options.XmlDirectory, refId + ".xml");
            if (!File.Exists(file))
                continue;

            var definition = XDocument.Load(file).Root!.Element("compounddef");
            if (definition is null)
                continue;

            var name = definition.Element("compoundname")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
                continue;

            // Anonymous and lambda-generated compounds carry '@' in their name.
            if (name.Contains('@'))
                continue;

            // Explicit and partial template specializations (TVariantValueCast<T*, ...>) are
            // implementation detail; the primary template is documented under its own name.
            if (name.Contains('<'))
                continue;

            if (options.ExcludeTypes.Any(p => Regex.IsMatch(name, p)))
                continue;

            var compound = new Compound
            {
                RefId = refId,
                Name = name,
                Kind = kind,
                Definition = definition,
            };

            compound.HeaderPath = ResolveHeader(definition);
            if (compound.HeaderPath is not null &&
                options.ExcludePaths.Any(p => compound.HeaderPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            compound.Module = ModuleOf(compound.HeaderPath);

            _compounds[name] = compound;
            _knownUids.Add(Uid(name));
        }

        // Second pass: base/derived links, now that every type is known.
        foreach (var compound in _compounds.Values)
        {
            foreach (var baseRef in compound.Definition.Elements("basecompoundref"))
            {
                var baseName = baseRef.Value.Trim();
                if (!_derived.TryGetValue(baseName, out var list))
                    _derived[baseName] = list = [];
                list.Add(compound.Name);
            }
        }
    }

    private string? ResolveHeader(XElement definition)
    {
        var location = definition.Element("location");
        var file = (string?)location?.Attribute("file");
        if (file is null)
            return null;

        var full = Path.GetFullPath(file);
        var root = Path.GetFullPath(options.EngineRoot);
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            full = full[root.Length..].TrimStart('/', '\\');
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// Groups global-scope types by their engine module (the folder under Source/Engine).
    ///
    /// Apogee's C++ API is mostly global-scope, so there is no namespace tree to browse by. The
    /// module only shapes the table of contents — uids stay the true qualified C++ names, so an
    /// xref written as @Actor still resolves.
    /// </summary>
    private static string? ModuleOf(string? headerPath)
    {
        if (headerPath is null)
            return null;
        var parts = headerPath.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].Equals("Source", StringComparison.OrdinalIgnoreCase))
                continue;
            // Source/Engine/<Module>/..., Source/Editor/<Module>/...
            if (i + 2 < parts.Length)
                return parts[i + 1] + "/" + parts[i + 2];
            if (i + 1 < parts.Length)
                return parts[i + 1];
        }
        return null;
    }

    // ---- Page construction -------------------------------------------------

    /// <summary>
    /// Builds the pages for one compound: the type itself, plus one page per nested enum.
    ///
    /// Nested types need their own file — DocFX generates anchors only for members, so an enum
    /// left inline on its parent's page ends up with links pointing at a bookmark that is never
    /// emitted.
    /// </summary>
    private List<(string Uid, string Content)> BuildPage(Compound compound)
    {
        var references = new Dictionary<string, ApiReference>(StringComparer.Ordinal);
        var typeUid = Uid(compound.Name);
        var description = DoxygenDescription.Parse(
            compound.Definition.Element("briefdescription"),
            compound.Definition.Element("detaileddescription"));

        var typeItem = new ApiItem
        {
            Uid = typeUid,
            CommentId = (compound.Kind == "namespace" ? "N:" : "T:") + typeUid,
            Id = SimpleName(compound.Name),
            Name = SimpleName(compound.Name),
            NameWithType = SimpleName(compound.Name),
            FullName = compound.Name,
            Type = MapCompoundKind(compound.Kind),
            Language = Language,
            Namespace = EnclosingScope(compound.Name),
            Summary = description.Body,
            Source = compound.HeaderPath,
            SourceLine = (int?)compound.Definition.Element("location")?.Attribute("line") ?? 0,
            SyntaxContent = TypeSyntax(compound),
        };
        typeItem.SeeAlso.AddRange(description.SeeAlso);
        if (description.Deprecated is not null)
        {
            typeItem.IsDeprecated = true;
            typeItem.DeprecationMessage = description.Deprecated;
        }

        if (typeItem.Namespace is not null)
        {
            references[typeItem.Namespace] = new ApiReference
            {
                Uid = typeItem.Namespace,
                Name = typeItem.Namespace,
                FullName = typeItem.Namespace,
                CommentId = "N:" + typeItem.Namespace,
            };
        }

        foreach (var baseRef in compound.Definition.Elements("basecompoundref"))
        {
            var baseName = baseRef.Value.Trim();
            typeItem.Inheritance.Add(Uid(baseName));
            AddReference(references, baseName);
        }
        if (_derived.TryGetValue(compound.Name, out var derived))
        {
            foreach (var d in derived.OrderBy(d => d, StringComparer.Ordinal))
                typeItem.DerivedClasses.Add(Uid(d));
        }

        var produced = new List<ApiItem>();
        foreach (var member in EnumerateMembers(compound))
            produced.AddRange(BuildMember(compound, member, typeUid, references));

        // A type with neither documentation nor visible members is noise in the tree.
        if (produced.Count == 0 && typeItem.Summary is null && typeItem.DerivedClasses.Count == 0)
            return [];

        // Split out nested enums (and the values belonging to them) into pages of their own.
        var nestedEnums = produced.Where(i => i.Type == "Enum").Select(i => i.Uid).ToHashSet(StringComparer.Ordinal);
        var members = new List<ApiItem>();
        var enumPages = new Dictionary<string, List<ApiItem>>(StringComparer.Ordinal);

        foreach (var item in produced)
        {
            if (nestedEnums.Contains(item.Uid))
            {
                enumPages[item.Uid] = [item];
                continue;
            }
            if (item.Parent is not null && nestedEnums.Contains(item.Parent))
            {
                enumPages[item.Parent].Add(item);
                continue;
            }
            members.Add(item);
        }

        foreach (var uid in nestedEnums)
        {
            typeItem.Children.Add(uid);
            // The child now lives on its own page, so the parent needs a reference carrying
            // enough for the template to list it: a name, a kind, and its summary line.
            var nested = produced.First(i => i.Uid == uid);
            references[uid] = new ApiReference
            {
                Uid = uid,
                Name = nested.Name,
                FullName = nested.FullName,
                CommentId = "T:" + uid,
                Parent = typeUid,
                Type = "Enum",
                Summary = nested.Summary,
            };
        }
        foreach (var m in members)
            typeItem.Children.Add(m.Uid);

        var pages = new List<(string Uid, string Content)>();
        var items = new List<ApiItem> { typeItem };
        items.AddRange(members);
        pages.Add((typeUid, ManagedReferenceWriter.Write(items, references.Values.ToList())));

        foreach (var (uid, page) in enumPages)
        {
            pages.Add((uid, ManagedReferenceWriter.Write(page, [
                new ApiReference
                {
                    Uid = typeUid,
                    Name = SimpleName(compound.Name),
                    FullName = compound.Name,
                    CommentId = "T:" + typeUid,
                },
            ])));
        }

        return pages;
    }

    private IEnumerable<XElement> EnumerateMembers(Compound compound)
    {
        foreach (var section in compound.Definition.Elements("sectiondef"))
        {
            foreach (var member in section.Elements("memberdef"))
            {
                var prot = (string?)member.Attribute("prot");
                if (prot == "private" || (prot == "protected" && !options.IncludeProtected))
                    continue;

                // Protected members are only shown when documented. Undocumented ones are almost
                // always storage for the public accessors above them (_layer, _isActive, the
                // bitfields), and listing them buries the API a subclass author actually needs.
                if (prot == "protected" && !HasDocumentation(member))
                    continue;

                yield return member;
            }
        }
    }

    private static bool HasDocumentation(XElement member) =>
        !string.IsNullOrWhiteSpace(member.Element("briefdescription")?.Value)
        || !string.IsNullOrWhiteSpace(member.Element("detaileddescription")?.Value);

    private IEnumerable<ApiItem> BuildMember(Compound compound, XElement member, string parentUid,
        Dictionary<string, ApiReference> references)
    {
        var kind = (string?)member.Attribute("kind");
        var name = member.Element("name")?.Value.Trim();
        if (string.IsNullOrEmpty(name) || name.StartsWith('@'))
            yield break;

        var description = DoxygenDescription.Parse(
            member.Element("briefdescription"),
            member.Element("detaileddescription"));

        var location = member.Element("location");
        var line = (int?)location?.Attribute("line") ?? 0;

        switch (kind)
        {
            case "function":
            {
                var args = member.Element("argsstring")?.Value.Trim() ?? "()";
                var displayName = name + StripArgumentNames(args);
                var uid = $"{parentUid}.{EscapeUidSegment(name)}{ArgumentSuffix(member)}";

                var item = new ApiItem
                {
                    Uid = uid,
                    CommentId = "M:" + uid,
                    Id = name,
                    Parent = parentUid,
                    Name = displayName,
                    NameWithType = SimpleName(compound.Name) + "." + displayName,
                    FullName = compound.Name + "::" + displayName,
                    Type = FunctionKind(compound, name),
                    Language = Language,
                    Namespace = EnclosingScope(compound.Name),
                    Summary = description.Body,
                    Source = compound.HeaderPath,
                    SourceLine = line,
                    SyntaxContent = FunctionSyntax(member),
                };
                item.SeeAlso.AddRange(description.SeeAlso);
                if (description.Deprecated is not null)
                {
                    item.IsDeprecated = true;
                    item.DeprecationMessage = description.Deprecated;
                }

                foreach (var param in member.Elements("param"))
                {
                    var pname = param.Element("declname")?.Value.Trim()
                                ?? param.Element("defname")?.Value.Trim();
                    if (string.IsNullOrEmpty(pname))
                        continue;
                    var ptype = TextOf(param.Element("type"));
                    AddReference(references, ptype);
                    item.Parameters.Add(new ApiParameter
                    {
                        Name = pname,
                        Type = Uid(StripDecorations(ptype)),
                        Description = description.Parameters.GetValueOrDefault(pname),
                        DefaultValue = param.Element("defval")?.Value.Trim(),
                    });
                }

                var returnType = TextOf(member.Element("type"));
                if (!string.IsNullOrEmpty(returnType) && returnType != "void")
                {
                    AddReference(references, returnType);
                    item.ReturnType = Uid(StripDecorations(returnType));
                    item.ReturnDescription = description.Returns;
                }

                yield return item;
                break;
            }

            case "variable":
            {
                var uid = $"{parentUid}.{EscapeUidSegment(name)}";
                var varType = TextOf(member.Element("type"));
                AddReference(references, varType);
                var item = new ApiItem
                {
                    Uid = uid,
                    CommentId = "F:" + uid,
                    Id = name,
                    Parent = parentUid,
                    Name = name,
                    NameWithType = SimpleName(compound.Name) + "." + name,
                    FullName = compound.Name + "::" + name,
                    Type = "Field",
                    Language = Language,
                    Namespace = EnclosingScope(compound.Name),
                    Summary = description.Body,
                    Source = compound.HeaderPath,
                    SourceLine = line,
                    SyntaxContent = Declaration(member),
                    ReturnType = string.IsNullOrEmpty(varType) ? null : Uid(StripDecorations(varType)),
                };
                if (description.Deprecated is not null)
                {
                    item.IsDeprecated = true;
                    item.DeprecationMessage = description.Deprecated;
                }
                yield return item;
                break;
            }

            case "enum":
            {
                var enumUid = $"{parentUid}.{EscapeUidSegment(name)}";
                var enumItem = new ApiItem
                {
                    Uid = enumUid,
                    CommentId = "T:" + enumUid,
                    Id = name,
                    Parent = parentUid,
                    Name = name,
                    NameWithType = SimpleName(compound.Name) + "." + name,
                    FullName = compound.Name + "::" + name,
                    Type = "Enum",
                    Language = Language,
                    Namespace = EnclosingScope(compound.Name),
                    Summary = description.Body,
                    Source = compound.HeaderPath,
                    SourceLine = line,
                    SyntaxContent = $"enum {name}",
                };

                var values = new List<ApiItem>();
                foreach (var value in member.Elements("enumvalue"))
                {
                    var vname = value.Element("name")?.Value.Trim();
                    if (string.IsNullOrEmpty(vname))
                        continue;
                    var vdesc = DoxygenDescription.Parse(
                        value.Element("briefdescription"), value.Element("detaileddescription"));
                    var vuid = $"{enumUid}.{EscapeUidSegment(vname)}";
                    enumItem.Children.Add(vuid);
                    values.Add(new ApiItem
                    {
                        Uid = vuid,
                        CommentId = "F:" + vuid,
                        Id = vname,
                        Parent = enumUid,
                        Name = vname,
                        NameWithType = name + "." + vname,
                        FullName = compound.Name + "::" + name + "::" + vname,
                        Type = "Field",
                        Language = Language,
                        Namespace = EnclosingScope(compound.Name),
                        Summary = vdesc.Body,
                        SyntaxContent = vname + (value.Element("initializer")?.Value.Trim() is { Length: > 0 } init
                            ? " " + init
                            : string.Empty),
                    });
                }

                yield return enumItem;
                foreach (var value in values)
                    yield return value;
                break;
            }

            case "typedef":
            {
                var uid = $"{parentUid}.{EscapeUidSegment(name)}";
                yield return new ApiItem
                {
                    Uid = uid,
                    CommentId = "T:" + uid,
                    Id = name,
                    Parent = parentUid,
                    Name = name,
                    NameWithType = SimpleName(compound.Name) + "." + name,
                    FullName = compound.Name + "::" + name,
                    Type = "Field",
                    Language = Language,
                    Namespace = EnclosingScope(compound.Name),
                    Summary = description.Body,
                    Source = compound.HeaderPath,
                    SourceLine = line,
                    SyntaxContent = Declaration(member),
                };
                break;
            }
        }
    }

    // ---- Syntax rendering --------------------------------------------------

    private static string MapCompoundKind(string kind) => kind switch
    {
        "struct" => "Struct",
        "union" => "Struct",
        "namespace" => "Namespace",
        "interface" => "Interface",
        _ => "Class",
    };

    private static string TypeSyntax(Compound compound)
    {
        var keyword = compound.Kind switch
        {
            "struct" => "struct",
            "union" => "union",
            "namespace" => "namespace",
            "interface" => "class",
            _ => "class",
        };

        var templateParams = compound.Definition.Element("templateparamlist");
        var prefix = templateParams is null
            ? string.Empty
            : "template<" + string.Join(", ", templateParams.Elements("param").Select(p =>
            {
                var t = TextOf(p.Element("type"));
                var n = p.Element("declname")?.Value.Trim();
                return string.IsNullOrEmpty(n) ? t : $"{t} {n}";
            })) + ">\n";

        var bases = compound.Definition.Elements("basecompoundref")
            .Select(b => $"{(string?)b.Attribute("prot") ?? "public"} {b.Value.Trim()}")
            .ToList();

        var declaration = $"{prefix}{keyword} {SimpleName(compound.Name)}";
        if (bases.Count > 0)
            declaration += " : " + string.Join(", ", bases);
        return declaration;
    }

    private static string FunctionSyntax(XElement member)
    {
        var parts = new List<string>();
        if ((string?)member.Attribute("static") == "yes")
            parts.Add("static");
        if ((string?)member.Attribute("virt") == "virtual" || (string?)member.Attribute("virt") == "pure-virtual")
            parts.Add("virtual");
        if ((string?)member.Attribute("explicit") == "yes")
            parts.Add("explicit");
        if ((string?)member.Attribute("constexpr") == "yes")
            parts.Add("constexpr");

        var returnType = TextOf(member.Element("type"));
        if (!string.IsNullOrEmpty(returnType))
            parts.Add(returnType);

        var name = member.Element("name")?.Value.Trim() ?? string.Empty;
        var args = member.Element("argsstring")?.Value.Trim() ?? "()";
        parts.Add(name + args);

        var templateParams = member.Element("templateparamlist");
        var prefix = templateParams is null
            ? string.Empty
            : "template<" + string.Join(", ", templateParams.Elements("param").Select(p =>
            {
                var t = TextOf(p.Element("type"));
                var n = p.Element("declname")?.Value.Trim();
                return string.IsNullOrEmpty(n) ? t : $"{t} {n}";
            })) + ">\n";

        return prefix + string.Join(" ", parts);
    }

    private static string Declaration(XElement member)
    {
        var definition = member.Element("definition")?.Value.Trim();
        var args = member.Element("argsstring")?.Value.Trim();
        if (string.IsNullOrEmpty(definition))
            return (member.Element("name")?.Value ?? string.Empty).Trim();
        var initializer = member.Element("initializer")?.Value.Trim();
        var text = definition + args;
        if (!string.IsNullOrEmpty(initializer) && initializer.Length < 120)
            text += " " + initializer;
        return text;
    }

    /// <summary>Turns "(int32 index, bool force)" into "(int32, bool)" for the display name.</summary>
    private static string StripArgumentNames(string args)
    {
        var open = args.IndexOf('(');
        var close = args.LastIndexOf(')');
        if (open < 0 || close <= open)
            return "()";
        var inner = args.Substring(open + 1, close - open - 1).Trim();
        if (inner.Length == 0)
            return "()";

        var types = SplitTopLevel(inner).Select(a =>
        {
            var arg = a.Trim();
            var equals = arg.IndexOf('=');
            if (equals >= 0)
                arg = arg[..equals].Trim();
            // Drop the trailing identifier, keeping the type and its decorations.
            var match = Regex.Match(arg, @"^(?<type>.*?[\s&*])\s*(?<name>[A-Za-z_]\w*)$");
            return match.Success ? match.Groups["type"].Value.Trim() : arg;
        });

        return "(" + string.Join(", ", types) + ")";
    }

    /// <summary>Splits an argument list on commas that are not nested inside &lt;&gt;, () or [].</summary>
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<' or '(' or '[':
                    depth++;
                    break;
                case '>' or ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>
    /// Disambiguates overloads in the uid. C++ allows overloading on parameter types, so the bare
    /// name is not unique; DocFX requires uids to be.
    /// </summary>
    private static string ArgumentSuffix(XElement member)
    {
        var parameters = member.Elements("param").ToList();
        if (parameters.Count == 0)
            return string.Empty;
        var types = parameters.Select(p => EscapeUidSegment(StripDecorations(TextOf(p.Element("type")))));
        return "(" + string.Join(",", types) + ")";
    }

    private static string FunctionKind(Compound compound, string name)
    {
        var simple = SimpleName(compound.Name);
        if (name == simple)
            return "Constructor";
        if (name == "~" + simple)
            return "Method";
        return name.StartsWith("operator", StringComparison.Ordinal) ? "Operator" : "Method";
    }

    // ---- Naming ------------------------------------------------------------

    private static string TextOf(XElement? element)
    {
        if (element is null)
            return string.Empty;
        return Regex.Replace(element.Value.Trim(), @"\s+", " ");
    }

    /// <summary>Strips const/&amp;/*/template arguments to get at the underlying type name.</summary>
    private static string StripDecorations(string type)
    {
        var text = type;
        text = Regex.Replace(text, @"\b(const|volatile|mutable|struct|class|typename)\b", " ");
        var angle = text.IndexOf('<');
        if (angle > 0)
            text = text[..angle];
        text = text.Replace("*", " ").Replace("&", " ").Replace("...", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? type.Trim() : text;
    }

    private static string SimpleName(string qualified)
    {
        var index = qualified.LastIndexOf("::", StringComparison.Ordinal);
        return index < 0 ? qualified : qualified[(index + 2)..];
    }

    private static string? EnclosingScope(string qualified)
    {
        var index = qualified.LastIndexOf("::", StringComparison.Ordinal);
        return index < 0 ? null : Uid(qualified[..index]);
    }

    private static string Uid(string qualified) => qualified.Replace("::", ".").Trim();

    /// <summary>DocFX treats these characters structurally inside a uid, so they cannot survive raw.</summary>
    private static string EscapeUidSegment(string segment) =>
        segment.Replace("::", ".").Replace(" ", string.Empty).Replace("~", "dtor_");

    /// <summary>
    /// Turns a qualified C++ name into a safe file name. DocFX treats the output path as a URL
    /// fragment and rejects anything with characters a path cannot carry.
    /// </summary>
    private static string FileNameFor(string qualified)
    {
        var uid = Uid(qualified);
        var sb = new System.Text.StringBuilder(uid.Length);
        foreach (var c in uid)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');

        var name = sb.ToString();
        // Long specialization names would otherwise exceed the filesystem's per-name limit.
        if (name.Length > 120)
            name = name[..100] + "_" + Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(uid)))[..8];
        return name;
    }

    private void AddReference(Dictionary<string, ApiReference> references, string type)
    {
        if (string.IsNullOrEmpty(type))
            return;
        var stripped = StripDecorations(type);
        if (stripped.Length == 0)
            return;
        var uid = Uid(stripped);
        if (references.ContainsKey(uid))
            return;
        references[uid] = new ApiReference
        {
            Uid = uid,
            Name = SimpleName(stripped),
            FullName = stripped,
            IsExternal = !_knownUids.Contains(uid),
        };
    }

    // ---- Table of contents -------------------------------------------------

    private void WriteToc(IReadOnlyList<(string Uid, string Name, string Kind, string Module)> emitted)
    {
        var roots = new List<TocNode>();
        foreach (var group in emitted.GroupBy(e => e.Module).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var node = new TocNode { Name = group.Key };
            foreach (var entry in group.OrderBy(e => SimpleName(e.Name), StringComparer.Ordinal))
                node.Items.Add(new TocNode { Name = SimpleName(entry.Name), Uid = entry.Uid });
            roots.Add(node);
        }

        File.WriteAllText(Path.Combine(options.OutputDirectory, "toc.yml"), TocWriter.Write(roots));
    }
}
