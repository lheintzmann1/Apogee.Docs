namespace Apogee.DocGen.Lua;

using System.Text.RegularExpressions;

public sealed class SolParserOptions
{
    /// <summary>The C++ variable that holds the root Lua table, and the Lua name it is bound to.</summary>
    public string RootVariable { get; init; } = "apogee";
    public string RootPath { get; init; } = "Apogee";

    /// <summary>Engine repo root, for reporting source paths relative to it.</summary>
    public required string EngineRoot { get; init; }

    public NativeIndex Native { get; init; } = NativeIndex.Empty;
}

/// <summary>
/// Extracts the Lua API from the sol2 registration code in <c>Source/Engine/LuaScripting/Bindings</c>.
///
/// The bindings are ordinary C++ — there is no manifest and no annotation requirement — so the
/// names, tables, usertypes and metamethods are read straight out of the registration calls.
/// That makes the extracted surface exhaustive by construction: a binding cannot be added without
/// appearing here, and one that is deleted cannot linger in the docs. What the calls cannot state
/// (parameter names for a forwarded member pointer, return types behind a lambda) is filled in
/// from the C++ declaration via <see cref="NativeIndex"/>, then from explicit tags in the comment
/// above the binding.
/// </summary>
public sealed partial class SolParser(SolParserOptions options)
{
    private readonly Dictionary<string, LuaSymbol> _symbols = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    /// <summary>
    /// C++ enum name -> the name it is published under, where the two differ. Signatures are read
    /// from the C++ declaration, so a parameter of an enum published under another name would
    /// otherwise be typed as something Lua has never heard of.
    /// </summary>
    private readonly Dictionary<string, string> _publishedEnums = new(StringComparer.Ordinal);

    private RegistrarIndex _registrars = RegistrarIndex.Empty;

    public IReadOnlyList<string> Warnings => _warnings;

    public IReadOnlyList<LuaSymbol> Parse(IEnumerable<string> files)
    {
        var sources = new List<SourceText>();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                sources.Add(SourceText.Load(file));
            }
            catch (Exception ex)
            {
                _warnings.Add($"{file}: {ex.Message}");
            }
        }

        // Which table each registrar is handed, resolved across files before any of them is read:
        // a file that receives its table as a parameter cannot be understood on its own.
        _registrars = RegistrarIndex.Build(sources, options, _warnings);

        foreach (var source in sources)
        {
            try
            {
                ParseFile(source);
            }
            catch (Exception ex)
            {
                _warnings.Add($"{source.Path}: {ex.Message}");
            }
        }

        RenamePublishedEnums();

        foreach (var symbol in _symbols.Values)
        {
            symbol.Members.Sort((a, b) =>
            {
                var byKind = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
                return byKind != 0 ? byKind : string.CompareOrdinal(a.Name, b.Name);
            });
        }

        return _symbols.Values.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
    }

    private static int KindOrder(LuaMemberKind kind) => kind switch
    {
        LuaMemberKind.Constructor => 0,
        LuaMemberKind.Constant or LuaMemberKind.EnumValue => 1,
        LuaMemberKind.Field => 2,
        LuaMemberKind.Property => 3,
        LuaMemberKind.Function or LuaMemberKind.Method => 4,
        LuaMemberKind.Operator => 5,
        _ => 6,
    };

    // ---- File scanning -----------------------------------------------------

    private void ParseFile(SourceText source)
    {
        var code = source.Code;
        var group = GroupOf(source.Path);
        var relative = Relative(source.Path);

        // Variable name -> Lua path. Every binding function receives the root table under the
        // same parameter name, so that one is bound for the whole file; any further table a
        // registrar receives is bound to its parameter as the scan enters that function.
        var tables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [options.RootVariable] = options.RootPath,
        };
        var usertypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var enums = new Dictionary<string, string>(StringComparer.Ordinal);

        var events = new List<(int Index, Action Apply)>();

        // Parameters holding a table created in another file (`sol::table& actor` <- Apogee.Actor).
        var parameters = new List<string>();
        foreach (var definition in RegistrarIndex.Definitions(source))
        {
            var captured = definition;
            events.Add((captured.Index, () =>
            {
                foreach (var name in parameters)
                    tables.Remove(name);
                parameters.Clear();

                foreach (var (parameter, path) in _registrars.Bindings(captured, options.RootVariable))
                {
                    tables[parameter] = path;
                    parameters.Add(parameter);
                }
            }));
        }

        // create_named / create_named_table: a nested table, possibly assigned to a local.
        foreach (Match m in TableDeclPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                var owner = captured.Groups["owner"].Value;
                if (!tables.TryGetValue(owner, out var ownerPath))
                    ownerPath = owner == options.RootVariable ? options.RootPath : null!;
                if (ownerPath is null)
                    return;

                var name = captured.Groups["name"].Value;
                var path = ownerPath == string.Empty ? name : ownerPath + "." + name;
                var variable = captured.Groups["var"].Value;
                if (variable.Length > 0)
                    tables[variable] = path;

                var line = source.LineOf(captured.Index);
                var symbol = Declare(path, LuaSymbolKind.Module, relative, line, group);
                ApplyBanner(symbol, source, line);
            }));
        }

        foreach (var declaration in FindUsertypes(source))
        {
            var captured = declaration;
            events.Add((captured.Index, () =>
            {
                if (!tables.TryGetValue(captured.Owner, out var ownerPath))
                    return;
                foreach (var (name, context) in captured.Names)
                {
                    var path = ownerPath == string.Empty ? name : ownerPath + "." + name;
                    if (captured.Variable is { Length: > 0 })
                        usertypes[captured.Variable] = path;

                    var symbol = Declare(path, LuaSymbolKind.Class, relative, captured.Line, group);
                    // Record the unfolded type (Vector3Base<float>), not the local alias (VecT).
                    symbol.NativeType = context.Expand(captured.NativeType);
                    ApplyBanner(symbol, source, captured.Line);
                    ParseUsertypeBody(symbol, captured.Arguments, source, relative, context);
                }
            }));
        }

        foreach (Match m in NewEnumPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                if (!tables.TryGetValue(captured.Groups["owner"].Value, out var ownerPath))
                    return;
                var open = code.IndexOf('(', captured.Index);
                if (open < 0)
                    return;
                var inner = ReadBalanced(code, open, '(', ')');
                var parts = SplitTopLevel(inner).ToList();
                if (parts.Count == 0)
                    return;
                var name = Unquote(parts[0]);
                if (name is null)
                    return;

                var line = source.LineOf(captured.Index);
                var symbol = Declare(Join(ownerPath, name), LuaSymbolKind.Enum, relative, line, group);
                ApplyBanner(symbol, source, line);
                RecordPublishedEnum(captured.Groups["native"].Value, symbol);

                for (var i = 1; i + 1 < parts.Count; i += 2)
                {
                    var key = Unquote(parts[i]);
                    if (key is null)
                        continue;
                    AddMember(symbol, new LuaMember
                    {
                        Name = key,
                        Kind = LuaMemberKind.EnumValue,
                        ValueType = "integer",
                        ConstantValue = parts[i + 1].Trim(),
                        ReadOnly = true,
                        SourceFile = relative,
                        SourceLine = line,
                    });
                }
            }));
        }

        // PublishEnum(apogee, "Key", keys) — the project's own helper for large constant tables.
        foreach (Match m in PublishEnumPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                if (!tables.TryGetValue(captured.Groups["owner"].Value, out var ownerPath))
                    return;
                var name = captured.Groups["name"].Value;
                var line = source.LineOf(captured.Index);
                var array = captured.Groups["array"].Value;
                var symbol = Declare(Join(ownerPath, name), LuaSymbolKind.Enum, relative, line, group);
                ApplyBanner(symbol, source, line);
                RecordPublishedEnum(PairArrayType(source, array), symbol);

                foreach (var (key, value, valueLine) in ReadPairArray(source, array))
                {
                    AddMember(symbol, new LuaMember
                    {
                        Name = key,
                        Kind = LuaMemberKind.EnumValue,
                        ValueType = "integer",
                        ConstantValue = value,
                        ReadOnly = true,
                        Description = DocComment.Parse(source.CommentAbove(valueLine)).Summary,
                        SourceFile = relative,
                        SourceLine = valueLine,
                    });
                }
            }));
        }

        foreach (Match m in SetFunctionPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                var variable = captured.Groups["var"].Value;
                var open = code.IndexOf('(', captured.Index);
                if (open < 0)
                    return;
                var parts = SplitTopLevel(ReadBalanced(code, open, '(', ')')).ToList();
                if (parts.Count < 2)
                    return;
                var name = Unquote(parts[0]);
                if (name is null)
                    return;

                var line = source.LineOf(captured.Index);
                var target = ResolveTarget(variable, tables, usertypes, enums, relative, line, group);
                if (target is null)
                    return;

                var member = BuildCallable(name, parts[1], source, line, relative,
                    target.Kind == LuaSymbolKind.Class, ContextOf(target));
                AddMember(target, member);
            }));
        }

        foreach (Match m in SetPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                var open = code.IndexOf('(', captured.Index);
                if (open < 0)
                    return;
                var parts = SplitTopLevel(ReadBalanced(code, open, '(', ')')).ToList();
                if (parts.Count < 2)
                    return;
                var name = Unquote(parts[0]);
                if (name is null)
                    return;

                var line = source.LineOf(captured.Index);
                var target = ResolveTarget(captured.Groups["var"].Value, tables, usertypes, enums,
                    relative, line, group);
                if (target is null)
                    return;

                AddMember(target, BuildValue(name, parts[1], source, line, relative, ContextOf(target)));
            }));
        }

        foreach (Match m in IndexAssignPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                var line = source.LineOf(captured.Index);
                var target = ResolveTarget(captured.Groups["var"].Value, tables, usertypes, enums,
                    relative, line, group);
                if (target is null)
                    return;

                var semicolon = code.IndexOf(';', captured.Index);
                var value = semicolon < 0
                    ? string.Empty
                    : code[(captured.Index + captured.Length)..semicolon].Trim();
                AddMember(target, BuildValue(captured.Groups["name"].Value, value, source, line, relative,
                    ContextOf(target)));
            }));
        }

        foreach (var (_, apply) in events.OrderBy(e => e.Index))
            apply();
    }

    /// <summary>
    /// The type context for a symbol reached through a variable rather than at its declaration
    /// (a later `t["Name"] = ...`). Only the enclosing type is known at that point, which is
    /// enough for the self-type rule.
    /// </summary>
    private static TypeContext ContextOf(LuaSymbol symbol) =>
        symbol.NativeType is null
            ? TypeContext.None
            : new TypeContext { SelfNative = symbol.NativeType, SelfLua = symbol.Name };

    private LuaSymbol? ResolveTarget(string variable,
        Dictionary<string, string> tables,
        Dictionary<string, string> usertypes,
        Dictionary<string, string> enums,
        string file,
        int line,
        string group)
    {
        if (usertypes.TryGetValue(variable, out var usertypePath))
            return _symbols.GetValueOrDefault(usertypePath);
        if (enums.TryGetValue(variable, out var enumPath))
            return _symbols.GetValueOrDefault(enumPath);
        if (tables.TryGetValue(variable, out var tablePath))
            // A registrar that receives its table as a parameter can be read before the file that
            // creates the table, so the symbol is opened here; the declaration corrects its
            // location and kind whenever it is reached.
            return Ensure(tablePath, LuaSymbolKind.Module, file, line, group);
        return null;
    }

    // ---- Usertypes ---------------------------------------------------------

    /// <summary>
    /// How a C++ type written in a binding should be read.
    ///
    /// Generic registration helpers (the Vector2/3/4Base pattern) write their members in terms of
    /// a local alias and a template parameter — <c>using VecT = Vector3Base&lt;T&gt;</c> — neither
    /// of which means anything to a Lua author. Substitutions unfold those back to the concrete
    /// type the call site instantiated, and <see cref="SelfNative"/> lets a member that returns
    /// the enclosing type render as its Lua name (`Float3`) instead of its C++ one.
    /// </summary>
    private sealed class TypeContext
    {
        public Dictionary<string, string> Substitutions { get; init; } = new(StringComparer.Ordinal);
        public string? SelfNative { get; init; }
        public string? SelfLua { get; init; }

        public static readonly TypeContext None = new();

        public string Expand(string type)
        {
            if (Substitutions.Count == 0)
                return type;
            var text = type;
            // Aliases can refer to one another; a couple of passes settles every case here, and
            // the bound stops a self-referential alias from looping.
            for (var pass = 0; pass < 4; pass++)
            {
                var before = text;
                foreach (var (from, to) in Substitutions)
                    text = Regex.Replace(text, @"\b" + Regex.Escape(from) + @"\b", to);
                if (text == before)
                    break;
            }
            return text;
        }

        public string Map(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return "any";
            var expanded = Expand(type);
            // SelfNative may itself be an alias (VecT), so unfold it before comparing.
            if (SelfNative is not null && SelfLua is not null && Bare(expanded) == Bare(Expand(SelfNative)))
                return SelfLua;
            return LuaTypes.Map(expanded);
        }

        private static string Bare(string type)
        {
            var text = Regex.Replace(type, @"\b(const|volatile|struct|class|typename)\b", " ")
                .Replace("*", " ").Replace("&", " ").Trim();
            var angle = text.IndexOf('<');
            if (angle > 0)
                text = text[..angle];
            return text.Trim();
        }
    }

    private sealed record UsertypeDeclaration(
        int Index,
        int Line,
        string Owner,
        string? Variable,
        string NativeType,
        IReadOnlyList<(string Name, TypeContext Context)> Names,
        string Arguments);

    private IEnumerable<UsertypeDeclaration> FindUsertypes(SourceText source)
    {
        var code = source.Code;
        foreach (Match m in UsertypePattern().Matches(code))
        {
            var angle = code.IndexOf('<', m.Index + m.Groups["owner"].Length);
            if (angle < 0)
                continue;
            var nativeType = ReadBalanced(code, angle, '<', '>').Trim();
            var open = code.IndexOf('(', angle);
            if (open < 0)
                continue;

            var arguments = ReadBalanced(code, open, '(', ')');
            var parts = SplitTopLevel(arguments).ToList();
            if (parts.Count == 0)
                continue;

            var aliases = CollectAliases(source);
            var nameArgument = parts[0].Trim();
            var names = new List<(string Name, TypeContext Context)>();
            var literal = Unquote(nameArgument);
            if (literal is not null)
            {
                names.Add((literal, new TypeContext
                {
                    Substitutions = new Dictionary<string, string>(aliases, StringComparer.Ordinal),
                    SelfNative = nativeType,
                    SelfLua = literal,
                }));
            }
            else
            {
                // The name is a parameter of a generic registration helper (the Vector2/3/4Base
                // pattern): recover the literals, and the type each was instantiated with, from
                // the helper's call sites.
                names.AddRange(ResolveNamesFromCallSites(source, nameArgument, aliases, nativeType));
                if (names.Count == 0)
                {
                    _warnings.Add(
                        $"{Relative(source.Path)}:{source.LineOf(m.Index)}: could not resolve the Lua name for " +
                        $"usertype '{nativeType}' (argument '{nameArgument}'); add an @luaname tag above it.");
                    continue;
                }
            }

            yield return new UsertypeDeclaration(
                m.Index,
                source.LineOf(m.Index),
                m.Groups["owner"].Value,
                m.Groups["var"].Success ? m.Groups["var"].Value : null,
                nativeType,
                names,
                string.Join(",", parts.Skip(1)));
        }
    }

    /// <summary>
    /// Finds the string literals a helper is called with, for a usertype whose Lua name arrives as
    /// a parameter rather than a literal, along with the template argument of each call site.
    /// </summary>
    private static IEnumerable<(string Name, TypeContext Context)> ResolveNamesFromCallSites(
        SourceText source, string parameterName, Dictionary<string, string> aliases, string nativeType)
    {
        var code = source.Code;
        var declaration = Regex.Match(code,
            @"\b(?<fn>\w+)\s*\((?<params>[^;{)]*\bconst\s+char\s*\*\s*" + Regex.Escape(parameterName) + @"\b[^;{)]*)\)");
        if (!declaration.Success)
            yield break;

        var function = declaration.Groups["fn"].Value;
        var parameters = SplitTopLevel(declaration.Groups["params"].Value).ToList();
        var position = parameters.FindIndex(p => Regex.IsMatch(p, @"\b" + Regex.Escape(parameterName) + @"\s*$"));
        if (position < 0)
            yield break;

        // The template parameter the helper is generic over, so a call site's <float> can be
        // substituted for the bare T that its members are written in terms of.
        var templateParameter = LastMatchBefore(code, declaration.Index, @"template\s*<\s*typename\s+(?<name>\w+)\s*>");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match call in Regex.Matches(code, @"\b" + Regex.Escape(function) + @"\s*(?<targs><[^;>]*>)?\s*\("))
        {
            if (call.Index == declaration.Index)
                continue;
            var open = code.IndexOf('(', call.Index + function.Length);
            if (open < 0)
                continue;
            var arguments = SplitTopLevel(ReadBalanced(code, open, '(', ')')).ToList();
            if (position >= arguments.Count)
                continue;
            var literal = Unquote(arguments[position]);
            if (literal is null || !seen.Add(literal))
                continue;

            var substitutions = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
            var templateArgument = call.Groups["targs"].Success
                ? call.Groups["targs"].Value.Trim('<', '>').Trim()
                : null;
            if (templateParameter is not null && !string.IsNullOrEmpty(templateArgument))
                substitutions[templateParameter] = templateArgument;

            yield return (literal, new TypeContext
            {
                Substitutions = substitutions,
                SelfNative = nativeType,
                SelfLua = literal,
            });
        }
    }

    /// <summary>Local type aliases (`using VecT = Vector3Base&lt;T&gt;;`) declared in the file.</summary>
    private static Dictionary<string, string> CollectAliases(SourceText source)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(source.Code, @"\busing\s+(?<alias>\w+)\s*=\s*(?<type>[^;=]+);"))
        {
            var alias = m.Groups["alias"].Value;
            var type = Regex.Replace(m.Groups["type"].Value.Trim(), @"\s+", " ");
            // A self-referential alias would make expansion loop.
            if (!Regex.IsMatch(type, @"\b" + Regex.Escape(alias) + @"\b"))
                aliases[alias] = type;
        }
        return aliases;
    }

    private static string? LastMatchBefore(string text, int index, string pattern)
    {
        string? found = null;
        foreach (Match m in Regex.Matches(text, pattern))
        {
            if (m.Index >= index)
                break;
            found = m.Groups["name"].Value;
        }
        return found;
    }

    private void ParseUsertypeBody(LuaSymbol symbol, string arguments, SourceText source, string relative,
        TypeContext context)
    {
        var parts = SplitTopLevel(arguments).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        for (var i = 0; i < parts.Count; i++)
        {
            var entry = parts[i];

            if (entry.StartsWith("sol::constructors", StringComparison.Ordinal))
            {
                AddConstructors(symbol, entry, source, relative, context);
                continue;
            }
            if (entry.StartsWith("sol::base_classes", StringComparison.Ordinal)
                || entry.StartsWith("sol::no_constructor", StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 >= parts.Count)
                break;
            var value = parts[i + 1];
            i++;

            var line = LineOfArgument(source, entry);
            var meta = MetaFunctionPattern().Match(entry);
            if (meta.Success)
            {
                var metamethod = LuaTypes.MetaFunction(meta.Groups["name"].Value);
                if (metamethod is null)
                    continue;
                var op = BuildCallable(metamethod, value, source, line, relative, true, context);
                var display = LuaTypes.OperatorSymbol(metamethod);
                if (display is not null)
                    op.Description ??= $"Metamethod for `{display}`.";
                AddMember(symbol, Retype(op, LuaMemberKind.Operator));
                continue;
            }

            var name = Unquote(entry);
            if (name is null)
                continue;

            AddMember(symbol, BuildUsertypeMember(name, value, source, line, relative, context));
        }
    }

    private void AddConstructors(LuaSymbol symbol, string entry, SourceText source, string relative,
        TypeContext context)
    {
        var angle = entry.IndexOf('<');
        if (angle < 0)
            return;
        var signatures = SplitTopLevel(ReadBalanced(entry, angle, '<', '>')).ToList();
        var line = LineOfArgument(source, "sol::constructors");

        foreach (var signature in signatures)
        {
            var open = signature.IndexOf('(');
            var close = signature.LastIndexOf(')');
            if (open < 0 || close <= open)
                continue;

            var member = new LuaMember
            {
                Name = "new",
                Kind = LuaMemberKind.Constructor,
                SourceFile = relative,
                SourceLine = line,
                Description = $"Creates a new `{symbol.Name}`.",
            };
            member.Returns.Add(symbol.Name);

            var arguments = signature[(open + 1)..close].Trim();
            if (arguments.Length > 0)
            {
                var index = 1;
                foreach (var argument in SplitTopLevel(arguments))
                {
                    member.Parameters.Add(new LuaParameter
                    {
                        Name = "arg" + index++,
                        Type = context.Map(argument),
                    });
                }
            }

            // sol2 exposes every constructor through one entry point, so they are overloads of
            // a single `new` rather than distinct members.
            var existing = symbol.Members.FirstOrDefault(m => m.Kind == LuaMemberKind.Constructor);
            if (existing is null)
                AddMember(symbol, member);
            else
                existing.AdditionalSignatures.Add(RenderSignature(symbol.Name + ".new", member));
        }
    }

    private LuaMember BuildUsertypeMember(string name, string value, SourceText source, int line, string relative,
        TypeContext context)
    {
        var doc = DocComment.Parse(source.CommentAbove(line));
        var trimmed = value.Trim();

        // sol::var exposes a static data member as a plain (read-only in practice) table entry.
        if (trimmed.StartsWith("sol::var", StringComparison.Ordinal))
        {
            var inner = InnerCall(trimmed);
            var native = ResolveNative(inner, context);
            return Finish(new LuaMember
            {
                Name = name,
                Kind = LuaMemberKind.Constant,
                ValueType = native is null ? "any" : context.Map(native.ReturnType),
                ReadOnly = true,
                Description = doc.Summary ?? native?.Summary,
                SourceFile = relative,
                SourceLine = line,
            }, doc);
        }

        if (trimmed.StartsWith("sol::property", StringComparison.Ordinal)
            || trimmed.StartsWith("sol::readonly_property", StringComparison.Ordinal))
        {
            var inner = SplitTopLevel(InnerCall(trimmed)).FirstOrDefault() ?? string.Empty;
            var native = ResolveNative(inner, context);
            return Finish(new LuaMember
            {
                Name = name,
                Kind = LuaMemberKind.Property,
                ValueType = native is null ? "any" : context.Map(native.ReturnType),
                ReadOnly = trimmed.StartsWith("sol::readonly_property", StringComparison.Ordinal),
                Description = doc.Summary ?? native?.Summary,
                SourceFile = relative,
                SourceLine = line,
            }, doc);
        }

        if (trimmed.StartsWith("sol::readonly", StringComparison.Ordinal))
        {
            var inner = InnerCall(trimmed);
            var native = ResolveNative(inner, context);
            return Finish(new LuaMember
            {
                Name = name,
                Kind = LuaMemberKind.Field,
                ValueType = native is null ? "any" : context.Map(native.ReturnType),
                ReadOnly = true,
                Description = doc.Summary ?? native?.Summary,
                SourceFile = relative,
                SourceLine = line,
            }, doc);
        }

        // A bare member pointer is either a data member (a field) or a method; the C++ index is
        // what distinguishes them, so an unresolved pointer stays a field with an unknown type.
        var pointer = MemberPointerPattern().Match(trimmed);
        if (pointer.Success)
        {
            var resolved = ResolveNative(trimmed, context);
            if (resolved is { IsFunction: false })
            {
                return Finish(new LuaMember
                {
                    Name = name,
                    Kind = LuaMemberKind.Field,
                    ValueType = context.Map(resolved.ReturnType),
                    Description = doc.Summary ?? resolved.Summary,
                    SourceFile = relative,
                    SourceLine = line,
                }, doc);
            }
        }

        return BuildCallable(name, value, source, line, relative, true, context);
    }

    // ---- Callables ---------------------------------------------------------

    private LuaMember BuildCallable(string name, string value, SourceText source, int line, string relative,
        bool isMethod, TypeContext context)
    {
        var doc = DocComment.Parse(source.CommentAbove(line));
        var member = new LuaMember
        {
            Name = name,
            Kind = isMethod ? LuaMemberKind.Method : LuaMemberKind.Function,
            SourceFile = relative,
            SourceLine = line,
            Description = doc.Summary,
            Remarks = doc.Remarks,
            Example = doc.Example,
        };

        var signatures = ExpandOverloads(value.Trim()).ToList();
        for (var i = 0; i < signatures.Count; i++)
        {
            var target = i == 0 ? member : new LuaMember { Name = name, Kind = member.Kind };
            ApplySignature(target, signatures[i], context);
            if (i > 0)
                member.AdditionalSignatures.Add(RenderSignature(name, target));
        }

        if (signatures.Count == 0)
            member.SignatureInferred = true;

        return Finish(member, doc);
    }

    private static IEnumerable<string> ExpandOverloads(string expression)
    {
        if (expression.StartsWith("sol::overload", StringComparison.Ordinal))
        {
            foreach (var part in SplitTopLevel(InnerCall(expression)))
                yield return part.Trim();
            yield break;
        }
        yield return expression;
    }

    private void ApplySignature(LuaMember member, string expression, TypeContext context)
    {
        // static_cast<Sig>(&T::M) and sol::resolve<Sig>(&T::M) pick one C++ overload. The cast
        // spells out the exact signature, which beats anything that could be inferred, so it is
        // used directly — with parameter names borrowed from the declaration when they line up.
        var cast = OverloadCastPattern().Match(expression);
        if (cast.Success && ApplyCastSignature(member, cast.Groups["sig"].Value, expression, context))
            return;

        var lambda = LambdaPattern().Match(expression);
        if (lambda.Success)
        {
            var open = expression.IndexOf('(', lambda.Index + lambda.Length - 1);
            if (open >= 0)
            {
                var parameters = ReadBalanced(expression, open, '(', ')');
                var index = 1;
                foreach (var parameter in SplitTopLevel(parameters))
                {
                    var text = parameter.Trim();
                    if (text.Length == 0)
                        continue;

                    // sol::this_state is an implementation detail sol2 injects; it is not a Lua argument.
                    if (text.Contains("sol::this_state", StringComparison.Ordinal))
                        continue;

                    var declaration = ParameterPattern().Match(text);
                    var pname = declaration.Success ? declaration.Groups["name"].Value : "arg" + index;
                    var ptype = declaration.Success ? declaration.Groups["type"].Value : text;

                    if (text.Contains("sol::variadic_args", StringComparison.Ordinal))
                    {
                        member.Parameters.Add(new LuaParameter { Name = "...", Type = "any" });
                        index++;
                        continue;
                    }

                    member.Parameters.Add(new LuaParameter { Name = pname, Type = context.Map(ptype) });
                    index++;
                }
            }

            var trailing = TrailingReturnPattern().Match(expression);
            if (trailing.Success)
            {
                member.Returns.Add(context.Map(trailing.Groups["type"].Value));
                return;
            }

            // No trailing return type, so the lambda's own body is the only clue. Most bindings
            // are a one-line forward — `[]() { return Time::GetDeltaTime(); }` — and the thing
            // being forwarded to is in the C++ index with its real return type.
            var forwarded = ForwardedExpressionPattern().Match(expression);
            if (forwarded.Success)
            {
                var forwardedTo = options.Native.Lookup(forwarded.Groups["target"].Value);
                if (forwardedTo is not null
                    && !string.IsNullOrEmpty(forwardedTo.ReturnType)
                    && forwardedTo.ReturnType != "void")
                {
                    member.Returns.Add(context.Map(forwardedTo.ReturnType));
                    member.ReturnDescription ??= forwardedTo.Returns;
                    member.Description ??= forwardedTo.Summary;
                }
            }
            return;
        }

        var native = ResolveNative(expression, context);
        if (native is null)
        {
            member.SignatureInferred = true;
            return;
        }

        foreach (var (pname, ptype) in native.Parameters)
        {
            member.Parameters.Add(new LuaParameter
            {
                Name = pname,
                Type = context.Map(ptype),
                Description = native.ParameterDocs.GetValueOrDefault(pname),
            });
        }
        if (!string.IsNullOrEmpty(native.ReturnType) && native.ReturnType != "void")
        {
            member.Returns.Add(context.Map(native.ReturnType));
            member.ReturnDescription = native.Returns;
        }
        member.Description ??= native.Summary;
    }

    /// <summary>
    /// Fills a member from an overload-selecting cast's signature. Returns false when the text
    /// does not parse as a function type, so the caller can fall back to its other strategies.
    /// </summary>
    private bool ApplyCastSignature(LuaMember member, string signature, string expression, TypeContext context)
    {
        var parsed = FunctionPointerPattern().Match(signature);
        if (!parsed.Success)
            parsed = FunctionTypePattern().Match(signature);
        if (!parsed.Success)
            return false;

        var native = ResolveNative(expression, context);
        var arguments = SplitTopLevel(parsed.Groups["args"].Value)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && a != "void")
            .ToList();

        // The cast has types but no names; the declaration has both. Use its names only when the
        // arity matches, since a mismatch means the lookup landed on a different overload.
        var named = native is not null && native.Parameters.Count == arguments.Count;

        for (var i = 0; i < arguments.Count; i++)
        {
            var pname = named ? native!.Parameters[i].Name : "arg" + (i + 1);
            member.Parameters.Add(new LuaParameter
            {
                Name = pname,
                Type = context.Map(arguments[i]),
                Description = named ? native!.ParameterDocs.GetValueOrDefault(pname) : null,
            });
        }

        var returnType = parsed.Groups["ret"].Value.Trim();
        if (returnType.Length > 0 && returnType != "void")
        {
            member.Returns.Add(context.Map(returnType));
            member.ReturnDescription ??= native?.Returns;
        }

        member.Description ??= native?.Summary;
        return true;
    }

    private LuaMember BuildValue(string name, string value, SourceText source, int line, string relative,
        TypeContext context)
    {
        var trimmed = value.Trim();
        if (LambdaPattern().IsMatch(trimmed)
            || MemberPointerPattern().IsMatch(trimmed)
            || trimmed.StartsWith("sol::overload", StringComparison.Ordinal))
        {
            return BuildCallable(name, trimmed, source, line, relative, false, context);
        }

        var doc = DocComment.Parse(source.CommentAbove(line));
        var cast = StaticCastPattern().Match(trimmed);
        return Finish(new LuaMember
        {
            Name = name,
            Kind = LuaMemberKind.Constant,
            ValueType = cast.Success ? context.Map(cast.Groups["type"].Value) : InferLiteralType(trimmed),
            ConstantValue = trimmed.Length is > 0 and < 100 ? trimmed : null,
            ReadOnly = true,
            Description = doc.Summary,
            SourceFile = relative,
            SourceLine = line,
        }, doc);
    }

    private static string InferLiteralType(string value)
    {
        if (value.StartsWith('"'))
            return "string";
        if (value is "true" or "false")
            return "boolean";
        if (Regex.IsMatch(value, @"^-?\d+$"))
            return "integer";
        if (Regex.IsMatch(value, @"^-?\d*\.\d+f?$"))
            return "number";
        return "any";
    }

    /// <summary>Overlays explicit tags onto whatever the parser inferred; tags always win.</summary>
    private static LuaMember Finish(LuaMember member, DocComment doc)
    {
        if (doc.Parameters.Count > 0)
        {
            var declared = doc.Parameters
                .Select(p => new LuaParameter
                {
                    Name = p.Name,
                    Type = p.Type,
                    Description = p.Description,
                    Optional = p.Optional,
                })
                .ToList();

            // Tags name the parameters; if the count matches what the lambda showed, keep the
            // inferred types where the tag left them unspecified.
            if (declared.Count == member.Parameters.Count)
            {
                for (var i = 0; i < declared.Count; i++)
                {
                    if (declared[i].Type == "any")
                        declared[i].Type = member.Parameters[i].Type;
                }
            }

            member.Parameters.Clear();
            member.Parameters.AddRange(declared);
            member.SignatureInferred = false;
        }
        else if (member.Parameters.Count > 0)
        {
            foreach (var parameter in member.Parameters)
                parameter.Description ??= doc.Parameters
                    .FirstOrDefault(p => p.Name == parameter.Name).Description;
        }

        if (doc.Returns.Count > 0)
        {
            member.Returns.Clear();
            foreach (var (type, description) in doc.Returns)
            {
                member.Returns.Add(type);
                if (description is not null)
                    member.ReturnDescription = member.ReturnDescription is null
                        ? description
                        : member.ReturnDescription + " " + description;
            }
        }

        if (doc.IsDeprecated)
        {
            member.Deprecated = true;
            member.DeprecationMessage = doc.Deprecated;
        }
        member.Remarks ??= doc.Remarks;
        member.Example ??= doc.Example;
        member.SeeAlso.AddRange(doc.SeeAlso);
        foreach (var overload in doc.Overloads)
            member.AdditionalSignatures.Add(overload);

        return member;
    }

    private static LuaMember Retype(LuaMember member, LuaMemberKind kind)
    {
        var copy = new LuaMember
        {
            Name = member.Name,
            Kind = kind,
            Description = member.Description,
            Remarks = member.Remarks,
            Example = member.Example,
            ReturnDescription = member.ReturnDescription,
            ValueType = member.ValueType,
            ConstantValue = member.ConstantValue,
            ReadOnly = member.ReadOnly,
            Deprecated = member.Deprecated,
            DeprecationMessage = member.DeprecationMessage,
            SourceFile = member.SourceFile,
            SourceLine = member.SourceLine,
            SignatureInferred = member.SignatureInferred,
        };
        copy.Parameters.AddRange(member.Parameters);
        copy.Returns.AddRange(member.Returns);
        copy.SeeAlso.AddRange(member.SeeAlso);
        copy.AdditionalSignatures.AddRange(member.AdditionalSignatures);
        return copy;
    }

    // ---- Native resolution -------------------------------------------------

    /// <summary>
    /// Pulls the qualified C++ name out of the ways a binding can refer to a member: a plain
    /// member pointer, a reference wrapper (<c>sol::var(std::ref(VecT::Up))</c>), or an
    /// overload-disambiguating cast.
    /// </summary>
    private static string? MemberTarget(string expression)
    {
        var text = expression.Trim();
        for (var pass = 0; pass < 3; pass++)
        {
            var wrapper = WrapperPattern().Match(text);
            if (!wrapper.Success)
                break;
            text = ReadBalanced(text, text.IndexOf('(', wrapper.Index), '(', ')').Trim();
        }

        text = text.TrimStart('&').Trim();
        return QualifiedNamePattern().IsMatch(text) ? text : null;
    }

    private NativeMember? ResolveNative(string expression, TypeContext context)
    {
        var qualified = MemberTarget(expression);
        if (qualified is null)
            return null;
        var direct = options.Native.Lookup(qualified);
        if (direct is not null)
            return direct;

        // The pointer may be written through a local alias (`&VecT::Length`); unfold it and retry.
        var expanded = context.Expand(qualified);
        if (expanded != qualified)
        {
            var viaAlias = options.Native.Lookup(expanded);
            if (viaAlias is not null)
                return viaAlias;
        }

        // Last resort: keep the member name but look it up on the usertype's own native type.
        if (context.SelfNative is null)
            return null;

        var separator = expanded.LastIndexOf("::", StringComparison.Ordinal);
        var memberName = separator < 0 ? expanded : expanded[(separator + 2)..];
        var self = context.Expand(context.SelfNative);
        var bare = self.Contains('<') ? self[..self.IndexOf('<')] : self;
        return options.Native.Lookup(bare, memberName);
    }

    // ---- Symbol bookkeeping ------------------------------------------------

    private LuaSymbol Ensure(string path, LuaSymbolKind kind, string file, int line, string group)
    {
        if (_symbols.TryGetValue(path, out var existing))
            return existing;
        var symbol = new LuaSymbol
        {
            Path = path,
            Kind = kind,
            SourceFile = file,
            SourceLine = line,
            Group = group,
        };
        _symbols[path] = symbol;
        return symbol;
    }

    /// <summary>
    /// The symbol for a table, usertype or enum at the point it is created.
    ///
    /// Members registered in another file open the symbol before this runs — a registrar receives
    /// the table as a parameter and the files are read in name order — so the declaration also
    /// restates what only it knows: the kind, and the location the page should point at.
    /// </summary>
    private LuaSymbol Declare(string path, LuaSymbolKind kind, string file, int line, string group)
    {
        var symbol = Ensure(path, kind, file, line, group);
        symbol.Kind = kind;
        symbol.SourceFile = file;
        symbol.SourceLine = line;
        symbol.Group = group;
        return symbol;
    }

    private static void AddMember(LuaSymbol symbol, LuaMember member)
    {
        var existing = symbol.Members.FindIndex(m => m.Name == member.Name && m.Kind == member.Kind);
        if (existing >= 0)
        {
            // A later registration of the same name replaces the earlier one, as it does at runtime.
            symbol.Members[existing] = member;
            return;
        }
        symbol.Members.Add(member);
    }

    /// <summary>
    /// Picks up the "---- Apogee.Time ----" banner comment that opens each binding section and
    /// uses the prose beneath it as the module description.
    /// </summary>
    private static void ApplyBanner(LuaSymbol symbol, SourceText source, int line)
    {
        if (symbol.Description is not null)
            return;

        var above = source.CommentAbove(line);
        if (above.Count == 0)
            return;

        var doc = DocComment.Parse(above);
        symbol.Description ??= doc.Summary;
        symbol.Remarks ??= doc.Remarks;
        symbol.Example ??= doc.Example;
        symbol.SeeAlso.AddRange(doc.SeeAlso);
    }

    /// <summary>Notes that a C++ enum reaches Lua under a different name.</summary>
    private void RecordPublishedEnum(string nativeType, LuaSymbol symbol)
    {
        var native = LuaTypes.Map(nativeType);
        if (native.Length == 0 || native == "any" || native == symbol.Name)
            return;
        _publishedEnums[native] = symbol.Name;
    }

    /// <summary>
    /// Retypes every signature written from a C++ enum that is published under another name —
    /// <c>InputGamepadIndex</c> is <c>Apogee.Gamepad</c> in Lua, and naming the C++ enum in a Lua
    /// signature points the reader at a type that does not exist.
    /// </summary>
    private void RenamePublishedEnums()
    {
        // A native name that is itself a bound Lua type is that type, not an alias for another.
        foreach (var path in _symbols.Values.Select(s => s.Name).ToList())
            _publishedEnums.Remove(path);

        if (_publishedEnums.Count == 0)
            return;

        foreach (var member in _symbols.Values.SelectMany(s => s.Members))
        {
            foreach (var parameter in member.Parameters)
                parameter.Type = RenamePublishedEnum(parameter.Type);
            for (var i = 0; i < member.Returns.Count; i++)
                member.Returns[i] = RenamePublishedEnum(member.Returns[i]);
            if (member.ValueType is not null)
                member.ValueType = RenamePublishedEnum(member.ValueType);
        }
    }

    /// <summary>Swaps the name while keeping the <c>?</c> and <c>[]</c> the signature carries.</summary>
    private string RenamePublishedEnum(string type)
    {
        var bare = type.TrimEnd('?');
        var suffix = type[bare.Length..];
        if (bare.EndsWith("[]", StringComparison.Ordinal))
        {
            bare = bare[..^2];
            suffix = "[]" + suffix;
        }
        return _publishedEnums.TryGetValue(bare, out var published) ? published + suffix : type;
    }

    private static string Join(string owner, string name) =>
        owner.Length == 0 ? name : owner + "." + name;

    private string Relative(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(options.EngineRoot);
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            full = full[root.Length..].TrimStart('/', '\\');
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// The binding domain (Core, Math, Physics...) a symbol is filed under in the table of contents.
    ///
    /// Usually the containing folder. The per-domain entry points sit one level up as
    /// `Lua&lt;Domain&gt;Bindings.cpp` and create the domain's root table there, so their name is
    /// used instead — otherwise `Apogee.Audio` would file itself away from the rest of Audio.
    /// `LuaBindings.cpp` names no domain and keeps the fallback.
    /// </summary>
    private static string GroupOf(string path)
    {
        var directory = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        if (directory.Length > 0 && !directory.Equals("Bindings", StringComparison.OrdinalIgnoreCase))
            return directory;

        var file = Path.GetFileNameWithoutExtension(path);
        const string prefix = "Lua";
        const string suffix = "Bindings";
        return file.StartsWith(prefix, StringComparison.Ordinal)
            && file.EndsWith(suffix, StringComparison.Ordinal)
            && file.Length > prefix.Length + suffix.Length
            ? file[prefix.Length..^suffix.Length]
            : "Engine";
    }

    private static int LineOfArgument(SourceText source, string fragment)
    {
        var index = source.Code.IndexOf(fragment, StringComparison.Ordinal);
        return index < 0 ? 0 : source.LineOf(index);
    }

    private static string RenderSignature(string name, LuaMember member)
    {
        var parameters = string.Join(", ", member.Parameters.Select(p => $"{p.Name}: {p.Type}"));
        var returns = member.Returns.Count > 0 ? ": " + string.Join(", ", member.Returns) : string.Empty;
        return $"{name}({parameters}){returns}";
    }

    // ---- Text helpers ------------------------------------------------------

    /// <summary>Reads the text between a delimiter at <paramref name="open"/> and its partner.</summary>
    internal static string ReadBalanced(string text, int open, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\')
                        i++;
                    i++;
                }
                continue;
            }
            if (c == openChar)
            {
                depth++;
            }
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0)
                    return text[(open + 1)..i];
            }
        }
        return text[(open + 1)..];
    }

    /// <summary>Splits on commas that are not nested inside brackets, braces, angles or a string.</summary>
    internal static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\')
                        i++;
                    i++;
                }
                continue;
            }
            switch (c)
            {
                case '(' or '[' or '{' or '<':
                    depth++;
                    break;
                case ')' or ']' or '}' or '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (start <= text.Length - 1)
            yield return text[start..];
    }

    private static string? Unquote(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];
        return null;
    }

    private static string InnerCall(string expression)
    {
        var open = expression.IndexOf('(');
        return open < 0 ? string.Empty : ReadBalanced(expression, open, '(', ')');
    }

    /// <summary>Finds a <c>static const std::pair&lt;const char*, E&gt; name[] = ...</c> declaration.</summary>
    private static Match PairArrayDeclaration(SourceText source, string variable) =>
        Regex.Match(source.Code,
            @"std::pair\s*<\s*const\s+char\s*\*\s*,\s*(?<native>[^>]*?)\s*>\s+"
            + Regex.Escape(variable) + @"\s*\[\s*\]\s*=");

    /// <summary>The C++ enum such a table holds the values of.</summary>
    private static string PairArrayType(SourceText source, string variable)
    {
        var declaration = PairArrayDeclaration(source, variable);
        return declaration.Success ? declaration.Groups["native"].Value : string.Empty;
    }

    /// <summary>Reads a <c>static const std::pair&lt;const char*, E&gt; name[] = {{"K", V}, ...}</c> table.</summary>
    private static IEnumerable<(string Key, string Value, int Line)> ReadPairArray(SourceText source, string variable)
    {
        var declaration = PairArrayDeclaration(source, variable);
        if (!declaration.Success)
            yield break;

        var brace = source.Code.IndexOf('{', declaration.Index + declaration.Length);
        if (brace < 0)
            yield break;

        var body = ReadBalanced(source.Code, brace, '{', '}');
        var offset = brace + 1;
        foreach (Match entry in Regex.Matches(body, @"\{\s*""(?<key>[^""]+)""\s*,\s*(?<value>[^}]+?)\s*\}"))
        {
            yield return (
                entry.Groups["key"].Value,
                entry.Groups["value"].Value.Trim(),
                source.LineOf(offset + entry.Index));
        }
    }

    // ---- Patterns ----------------------------------------------------------

    [GeneratedRegex(@"(?:(?:sol::table|auto)\s+(?<var>\w+)\s*=\s*)?\b(?<owner>\w+)\s*\.\s*create_named(?:_table)?\s*\(\s*""(?<name>[^""]+)""")]
    internal static partial Regex TableDeclPattern();

    [GeneratedRegex(@"(?:(?:auto|sol::usertype\s*<[^>]*>)\s+(?<var>\w+)\s*=\s*)?\b(?<owner>\w+)\s*\.\s*new_usertype\s*<")]
    private static partial Regex UsertypePattern();

    [GeneratedRegex(@"\b(?<owner>\w+)\s*\.\s*new_enum\s*(?:<\s*(?<native>[^>]*?)\s*>)?\s*\(")]
    private static partial Regex NewEnumPattern();

    [GeneratedRegex(@"\bPublishEnum\s*\(\s*(?<owner>\w+)\s*,\s*""(?<name>[^""]+)""\s*,\s*(?<array>\w+)\s*\)")]
    private static partial Regex PublishEnumPattern();

    [GeneratedRegex(@"\b(?<var>\w+)\s*\.\s*set_function\s*\(")]
    private static partial Regex SetFunctionPattern();

    [GeneratedRegex(@"\b(?<var>\w+)\s*\.\s*set\s*\(")]
    private static partial Regex SetPattern();

    [GeneratedRegex(@"\b(?<var>\w+)\s*\[\s*""(?<name>[^""]+)""\s*\]\s*=\s*")]
    private static partial Regex IndexAssignPattern();

    [GeneratedRegex(@"^sol::meta_function::(?<name>\w+)")]
    private static partial Regex MetaFunctionPattern();

    [GeneratedRegex(@"^&\s*(?<target>[\w:<>,\s]+?)\s*$")]
    private static partial Regex MemberPointerPattern();

    /// <summary>Wrappers that stand between the expression and the member it names.</summary>
    [GeneratedRegex(@"^(?:std::(?:ref|cref|addressof)|sol::(?:resolve|as_function)\s*<[^>]*>|static_cast\s*<.*>|const_cast\s*<[^>]*>)\s*\(")]
    private static partial Regex WrapperPattern();

    [GeneratedRegex(@"^[A-Za-z_]\w*(?:::[A-Za-z_]\w*)+$")]
    private static partial Regex QualifiedNamePattern();

    /// <summary>A cast that selects one C++ overload: <c>static_cast&lt;Sig&gt;(&amp;T::M)</c>.</summary>
    [GeneratedRegex(@"^(?:static_cast|sol::resolve)\s*<(?<sig>.*)>\s*\(")]
    private static partial Regex OverloadCastPattern();

    /// <summary>A function-pointer type: <c>void (*)(const VecT&amp;, VecT&amp;)</c>.</summary>
    [GeneratedRegex(@"^(?<ret>[^(]*?)\s*\(\s*[\w:]*\*\s*\)\s*\((?<args>.*)\)\s*(?:const)?\s*$")]
    private static partial Regex FunctionPointerPattern();

    /// <summary>A plain function type, as written inside <c>sol::resolve&lt;...&gt;</c>.</summary>
    [GeneratedRegex(@"^(?<ret>[^(]+?)\s*\((?<args>.*)\)\s*(?:const)?\s*$")]
    private static partial Regex FunctionTypePattern();

    [GeneratedRegex(@"^\s*\[[^\]]*\]\s*\(")]
    private static partial Regex LambdaPattern();

    [GeneratedRegex(@"->\s*(?<type>[\w:<>,\s\*&]+?)\s*\{")]
    private static partial Regex TrailingReturnPattern();

    /// <summary>A lambda body that is a single `return Something::Member...;`.</summary>
    [GeneratedRegex(@"\{\s*return\s+(?<target>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)+)\s*[({;]")]
    private static partial Regex ForwardedExpressionPattern();

    [GeneratedRegex(@"^(?<type>.*?[\s&*])\s*(?<name>[A-Za-z_]\w*)\s*(?:=[^,]*)?$")]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"^static_cast\s*<\s*(?<type>[^>]+)>")]
    private static partial Regex StaticCastPattern();
}
