namespace Apogee.DocGen.Lua;

using System.Text;
using Apogee.DocGen.Yaml;

public sealed class LuaOptions
{
    public required string OutputDirectory { get; init; }

    /// <summary>Where to write the LuaCATS definition file, or null to skip it.</summary>
    public string? DefinitionsPath { get; init; }
}

/// <summary>
/// Renders the extracted Lua API as DocFX pages, plus a LuaCATS definition file.
///
/// The definition file is not documentation output — it is the same model written in the format
/// the Lua language server reads, so a game author gets completion and signature help for the
/// engine API in their editor from the same extraction that produces the website.
/// </summary>
public sealed class LuaGenerator(LuaOptions options)
{
    private const string Language = "lua";

    public int Generate(IReadOnlyList<LuaSymbol> symbols)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        foreach (var symbol in symbols)
        {
            var (items, references) = BuildPage(symbol, symbols);
            var yaml = ManagedReferenceWriter.Write(items, references);
            File.WriteAllText(Path.Combine(options.OutputDirectory, Uid(symbol.Path) + ".yml"), yaml);
        }

        WriteToc(symbols);

        if (options.DefinitionsPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.DefinitionsPath)!);
            File.WriteAllText(options.DefinitionsPath, WriteDefinitions(symbols));
        }

        return symbols.Count;
    }

    // ---- DocFX pages -------------------------------------------------------

    private static (List<ApiItem> Items, List<ApiReference> References) BuildPage(
        LuaSymbol symbol, IReadOnlyList<LuaSymbol> all)
    {
        var references = new Dictionary<string, ApiReference>(StringComparer.Ordinal);
        var known = all.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var typeUid = Uid(symbol.Path);

        var summary = symbol.Description;
        if (symbol.NativeType is not null)
        {
            var note = $"Backed by the C++ type `{symbol.NativeType}`.";
            summary = string.IsNullOrEmpty(summary) ? note : summary + "\n\n" + note;
        }

        var typeItem = new ApiItem
        {
            Uid = typeUid,
            CommentId = "T:" + typeUid,
            Id = symbol.Name,
            Name = symbol.Name,
            NameWithType = symbol.Path,
            FullName = symbol.Path,
            Type = symbol.Kind == LuaSymbolKind.Enum ? "Enum" : "Class",
            Language = Language,
            Namespace = symbol.Parent is null ? null : Uid(symbol.Parent),
            Summary = summary,
            Remarks = symbol.Remarks,
            Example = symbol.Example,
            Source = symbol.SourceFile,
            SourceLine = symbol.SourceLine,
            SyntaxContent = SymbolSyntax(symbol),
        };
        typeItem.SeeAlso.AddRange(symbol.SeeAlso);

        // The enclosing table is shown as the "namespace"; give its uid a display name so the
        // page reads "Apogee" rather than the internal "lua.Apogee".
        if (symbol.Parent is not null)
        {
            references[Uid(symbol.Parent)] = new ApiReference
            {
                Uid = Uid(symbol.Parent),
                Name = symbol.Parent,
                FullName = symbol.Parent,
                CommentId = "N:" + Uid(symbol.Parent),
            };
        }

        var items = new List<ApiItem> { typeItem };

        foreach (var member in symbol.Members)
        {
            var memberUid = $"{typeUid}.{member.Name}";
            typeItem.Children.Add(memberUid);

            var item = new ApiItem
            {
                Uid = memberUid,
                CommentId = (IsCallable(member.Kind) ? "M:" : "F:") + memberUid,
                Id = member.Name,
                Parent = typeUid,
                Name = DisplayName(member),
                NameWithType = symbol.Name + "." + DisplayName(member),
                FullName = symbol.Path + "." + DisplayName(member),
                Type = MapKind(member.Kind),
                Language = Language,
                Namespace = symbol.Parent is null ? null : Uid(symbol.Parent),
                Summary = BuildMemberSummary(member),
                Remarks = member.Remarks,
                Example = member.Example,
                Source = member.SourceFile,
                SourceLine = member.SourceLine,
                SyntaxContent = MemberSyntax(symbol, member),
                IsDeprecated = member.Deprecated,
                DeprecationMessage = member.DeprecationMessage,
            };
            item.SeeAlso.AddRange(member.SeeAlso);

            foreach (var parameter in member.Parameters)
            {
                if (parameter.Name == "...")
                {
                    item.Parameters.Add(new ApiParameter { Name = "...", Type = "any", Description = "Variadic arguments." });
                    continue;
                }
                var type = parameter.Type;
                Reference(references, type, known);
                item.Parameters.Add(new ApiParameter
                {
                    Name = parameter.Name,
                    Type = TypeUid(type, known),
                    Description = parameter.Description,
                    Optional = parameter.Optional,
                });
            }

            if (member.Returns.Count > 0)
            {
                var type = string.Join(", ", member.Returns);
                Reference(references, member.Returns[0], known);
                item.ReturnType = member.Returns.Count == 1 ? TypeUid(member.Returns[0], known) : type;
                item.ReturnDescription = member.ReturnDescription;
            }
            else if (member.ValueType is not null && !IsCallable(member.Kind))
            {
                Reference(references, member.ValueType, known);
                item.ReturnType = TypeUid(member.ValueType, known);
            }

            items.Add(item);
        }

        return (items, references.Values.ToList());
    }

    private static string? BuildMemberSummary(LuaMember member)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(member.Description))
            parts.Add(member.Description);

        if (member.ReadOnly && member.Kind is LuaMemberKind.Field or LuaMemberKind.Property or LuaMemberKind.Constant)
            parts.Add("Read-only.");

        if (member.AdditionalSignatures.Count > 0)
        {
            var overloads = new StringBuilder("Additional forms:\n\n```lua\n");
            foreach (var signature in member.AdditionalSignatures)
                overloads.Append(signature).Append('\n');
            overloads.Append("```");
            parts.Add(overloads.ToString());
        }

        // Being explicit beats a silently-wrong signature: the reader needs to know when the
        // argument list was inferred from a lambda rather than declared.
        if (member.SignatureInferred && IsCallable(member.Kind) && member.Parameters.Count == 0
            && member.Returns.Count == 0)
        {
            parts.Add("> [!NOTE]\n> The signature for this binding could not be determined automatically. "
                      + "See the binding source for the exact arguments.");
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string SymbolSyntax(LuaSymbol symbol) => symbol.Kind switch
    {
        LuaSymbolKind.Enum => $"{symbol.Path}  -- table of integer constants",
        _ => symbol.Path,
    };

    private static string MemberSyntax(LuaSymbol symbol, LuaMember member)
    {
        var qualified = symbol.Path + (member.Kind == LuaMemberKind.Method ? ":" : ".") + member.Name;

        switch (member.Kind)
        {
            case LuaMemberKind.Constant:
            case LuaMemberKind.EnumValue:
            case LuaMemberKind.Field:
            case LuaMemberKind.Property:
            {
                var type = member.ValueType ?? "any";
                var value = member.ConstantValue is null ? string.Empty : $" -- = {member.ConstantValue}";
                return $"{symbol.Path}.{member.Name}: {type}{value}";
            }

            case LuaMemberKind.Constructor:
                return $"{symbol.Path}.new({Parameters(member)}) -> {symbol.Name}";

            case LuaMemberKind.Operator:
            {
                var display = LuaTypes.OperatorSymbol(member.Name);
                return display is null
                    ? $"{symbol.Path}.{member.Name}({Parameters(member)})"
                    : $"{display}  -- {member.Name}";
            }

            default:
            {
                var returns = member.Returns.Count > 0 ? " -> " + string.Join(", ", member.Returns) : string.Empty;
                return $"{qualified}({Parameters(member)}){returns}";
            }
        }
    }

    private static string Parameters(LuaMember member) =>
        string.Join(", ", member.Parameters.Select(p =>
            p.Name == "..." ? "..." : $"{p.Name}{(p.Optional ? "?" : string.Empty)}: {p.Type}"));

    private static string DisplayName(LuaMember member) => member.Kind switch
    {
        LuaMemberKind.Constructor => "new(" + string.Join(", ", member.Parameters.Select(p => p.Type)) + ")",
        LuaMemberKind.Operator => member.Name,
        _ when IsCallable(member.Kind) => member.Name + "(" + string.Join(", ", member.Parameters.Select(p => p.Type)) + ")",
        _ => member.Name,
    };

    private static bool IsCallable(LuaMemberKind kind) =>
        kind is LuaMemberKind.Function or LuaMemberKind.Method or LuaMemberKind.Constructor or LuaMemberKind.Operator;

    private static string MapKind(LuaMemberKind kind) => kind switch
    {
        LuaMemberKind.Constructor => "Constructor",
        LuaMemberKind.Operator => "Operator",
        LuaMemberKind.Property => "Property",
        LuaMemberKind.Field or LuaMemberKind.Constant or LuaMemberKind.EnumValue => "Field",
        _ => "Method",
    };

    private static string Uid(string path) => "lua." + path;

    /// <summary>
    /// Resolves a Lua type name to a uid so DocFX renders it as a link, leaving the primitives
    /// (number, string, table...) as plain text.
    /// </summary>
    private static string TypeUid(string type, HashSet<string> known)
    {
        var bare = type.TrimEnd('?', '[', ']');
        return known.Contains(bare) ? "lua.Apogee." + bare : type;
    }

    private static void Reference(Dictionary<string, ApiReference> references, string type, HashSet<string> known)
    {
        var uid = TypeUid(type, known);
        if (references.ContainsKey(uid))
            return;
        references[uid] = new ApiReference
        {
            Uid = uid,
            Name = type,
            FullName = type,
            IsExternal = uid == type,
        };
    }

    private void WriteToc(IReadOnlyList<LuaSymbol> symbols)
    {
        var roots = new List<TocNode>();
        foreach (var group in symbols.GroupBy(s => s.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var node = new TocNode { Name = group.Key };
            foreach (var symbol in group.OrderBy(s => s.Path, StringComparer.Ordinal))
                node.Items.Add(new TocNode { Name = symbol.Path, Uid = Uid(symbol.Path) });
            roots.Add(node);
        }

        File.WriteAllText(Path.Combine(options.OutputDirectory, "toc.yml"), TocWriter.Write(roots));
    }

    // ---- LuaCATS definitions -----------------------------------------------

    private static string WriteDefinitions(IReadOnlyList<LuaSymbol> symbols)
    {
        var sb = new StringBuilder();
        sb.Append("---@meta\n");
        sb.Append("--\n");
        sb.Append("-- Apogee Engine Lua API definitions.\n");
        sb.Append("--\n");
        sb.Append("-- Generated by Apogee.DocGen from the sol2 bindings. Do not edit by hand.\n");
        sb.Append("-- Point your Lua language server at this file to get completion and signature\n");
        sb.Append("-- help for the engine API (see manual/scripting/lua/editor-setup.md).\n");
        sb.Append("--\n\n");

        // Every table on the path must exist before a nested one can be assigned to it.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            foreach (var ancestor in Ancestors(symbol.Path))
            {
                if (!declared.Add(ancestor))
                    continue;
                sb.Append(ancestor.Contains('.') ? string.Empty : "---@class " + ancestor + "\n");
                sb.Append(ancestor.Contains('.') ? ancestor + " = " + ancestor + " or {}\n" : ancestor + " = {}\n");
            }
        }
        sb.Append('\n');

        foreach (var symbol in symbols.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            sb.Append("\n-- ").Append(new string('-', 74)).Append('\n');
            if (!string.IsNullOrEmpty(symbol.Description))
            {
                foreach (var line in symbol.Description.Split('\n'))
                    sb.Append("--- ").Append(line).Append('\n');
            }

            sb.Append("---@class ").Append(symbol.Path).Append('\n');
            foreach (var member in symbol.Members.Where(m => !IsCallable(m.Kind)))
            {
                sb.Append("---@field ").Append(member.Name).Append(' ').Append(member.ValueType ?? "any");
                if (!string.IsNullOrEmpty(member.Description))
                    sb.Append(' ').Append(FirstLine(member.Description));
                sb.Append('\n');
            }
            if (declared.Add(symbol.Path))
                sb.Append(symbol.Path).Append(" = {}\n");
            else
                sb.Append(symbol.Path).Append(" = ").Append(symbol.Path).Append(" or {}\n");

            foreach (var member in symbol.Members.Where(m => IsCallable(m.Kind)))
            {
                if (member.Kind == LuaMemberKind.Operator)
                    continue;

                sb.Append('\n');
                if (!string.IsNullOrEmpty(member.Description))
                {
                    foreach (var line in member.Description.Split('\n'))
                        sb.Append("--- ").Append(line).Append('\n');
                }
                if (member.Deprecated)
                    sb.Append("---@deprecated\n");

                foreach (var parameter in member.Parameters)
                {
                    if (parameter.Name == "...")
                    {
                        sb.Append("---@vararg any\n");
                        continue;
                    }
                    sb.Append("---@param ").Append(Sanitize(parameter.Name));
                    if (parameter.Optional)
                        sb.Append('?');
                    sb.Append(' ').Append(parameter.Type);
                    if (!string.IsNullOrEmpty(parameter.Description))
                        sb.Append(' ').Append(FirstLine(parameter.Description));
                    sb.Append('\n');
                }
                foreach (var returnType in member.Returns)
                    sb.Append("---@return ").Append(returnType).Append('\n');

                var name = member.Kind == LuaMemberKind.Constructor ? "new" : member.Name;
                var arguments = string.Join(", ",
                    member.Parameters.Select(p => p.Name == "..." ? "..." : Sanitize(p.Name)));
                sb.Append("function ").Append(symbol.Path).Append('.').Append(name)
                    .Append('(').Append(arguments).Append(") end\n");
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var parts = path.Split('.');
        for (var i = 1; i < parts.Length; i++)
            yield return string.Join('.', parts[..i]);
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? text : text[..index];
    }

    /// <summary>Keeps generated parameter names valid Lua identifiers.</summary>
    private static string Sanitize(string name)
    {
        if (name.Length == 0)
            return "arg";
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        var result = sb.ToString();
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return LuaKeywords.Contains(result) ? result + "_" : result;
    }

    private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if",
        "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };
}
