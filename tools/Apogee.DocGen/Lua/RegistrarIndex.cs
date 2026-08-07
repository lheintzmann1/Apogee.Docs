namespace Apogee.DocGen.Lua;

using System.Text.RegularExpressions;

/// <summary>One parameter of a registrar function, as written in its definition.</summary>
public sealed record RegistrarParameter(string Name, bool IsTable);

/// <summary>
/// A <c>void Register...(sol::state&amp; lua, sol::table&amp; apogee, sol::table&amp; actor) {</c>
/// definition, with the position it starts at so the parser can bind its parameters at the right
/// point in the file.
/// </summary>
public sealed record RegistrarDefinition(int Index, string Name, IReadOnlyList<RegistrarParameter> Parameters);

/// <summary>
/// Which Lua table each registrar function receives in each of its parameters.
///
/// A domain's table is created once — <c>apogee.create_named("Actor")</c> — and then handed to a
/// row of registrars that live in their own files, one per concern. Inside those files the table
/// is a plain <c>sol::table&amp;</c> parameter, so a file-local scan has no way to know that
/// <c>actor.set_function(...)</c> lands on <c>Apogee.Actor</c>, and every member registered that
/// way would be dropped. This resolves the call sites first: <c>Actors::RegisterLifecycle(lua,
/// apogee, actor)</c> in the hub file records that the third argument of a three-argument
/// <c>RegisterLifecycle</c> is <c>Apogee.Actor</c>, which is what the parser then seeds the
/// parameter with.
///
/// Functions are keyed by name and argument count. The qualifier is ignored — a call writes
/// <c>Actors::RegisterTransform</c> while the definition writes
/// <c>LuaBindings::Actors::RegisterTransform</c> — and the argument count is what separates the
/// two <c>RegisterTransform</c>s and the two <c>RegisterEnums</c>. A name that is nevertheless
/// reached with conflicting tables is dropped rather than guessed at, and reported.
/// </summary>
public sealed partial class RegistrarIndex
{
    /// <summary>Registrars that forward a table they received, so one pass cannot settle it.</summary>
    private const int MaxRounds = 4;

    private readonly Dictionary<string, Dictionary<int, string>> _arguments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);

    public static RegistrarIndex Empty { get; } = new();

    public static RegistrarIndex Build(IReadOnlyList<SourceText> sources, SolParserOptions options,
        ICollection<string> warnings)
    {
        var index = new RegistrarIndex();

        for (var round = 0; round < MaxRounds; round++)
        {
            var changed = false;
            foreach (var source in sources)
                changed |= index.Scan(source, options);
            if (!changed)
                break;
        }

        foreach (var key in index._ambiguous.Order(StringComparer.Ordinal))
            warnings.Add($"{key.Replace('/', '@')}: called with different tables in the same position; its members are unattributed.");

        return index;
    }

    /// <summary>The tables this definition's parameters hold, by parameter name.</summary>
    public IEnumerable<(string Parameter, string Path)> Bindings(RegistrarDefinition definition, string rootVariable)
    {
        if (!_arguments.TryGetValue(Key(definition.Name, definition.Parameters.Count), out var byPosition))
            yield break;

        foreach (var (position, path) in byPosition)
        {
            if (position >= definition.Parameters.Count)
                continue;
            var parameter = definition.Parameters[position];
            // The root table is already bound everywhere, and rebinding it would let a later
            // definition in the same file unbind it.
            if (!parameter.IsTable || parameter.Name.Length == 0 || parameter.Name == rootVariable)
                continue;
            yield return (parameter.Name, path);
        }
    }

    /// <summary>Records the tables passed at every registrar call site in one file.</summary>
    private bool Scan(SourceText source, SolParserOptions options)
    {
        var code = source.Code;
        var recorded = false;

        var tables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [options.RootVariable] = options.RootPath,
        };
        var bound = new List<string>();

        var events = new List<(int Index, Action Apply)>();

        foreach (var definition in Definitions(source))
        {
            var captured = definition;
            events.Add((captured.Index, () =>
            {
                // Parameters are scoped to their function; a name bound by the previous one must
                // not leak into this one.
                foreach (var name in bound)
                    tables.Remove(name);
                bound.Clear();

                foreach (var (parameter, path) in Bindings(captured, options.RootVariable))
                {
                    tables[parameter] = path;
                    bound.Add(parameter);
                }
            }));
        }

        foreach (Match m in SolParser.TableDeclPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                var variable = captured.Groups["var"].Value;
                if (variable.Length == 0)
                    return;
                if (!tables.TryGetValue(captured.Groups["owner"].Value, out var ownerPath))
                    return;
                var name = captured.Groups["name"].Value;
                tables[variable] = ownerPath.Length == 0 ? name : ownerPath + "." + name;
            }));
        }

        foreach (Match m in CallPattern().Matches(code))
        {
            var captured = m;
            events.Add((captured.Index, () =>
            {
                var arguments = SolParser.SplitTopLevel(captured.Groups["args"].Value)
                    .Select(a => a.Trim())
                    .ToList();
                if (arguments is [""])
                    return;

                for (var position = 0; position < arguments.Count; position++)
                {
                    if (!IdentifierPattern().IsMatch(arguments[position]))
                        continue;
                    if (!tables.TryGetValue(arguments[position], out var path))
                        continue;
                    recorded |= Record(captured.Groups["name"].Value, arguments.Count, position, path);
                }
            }));
        }

        foreach (var (_, apply) in events.OrderBy(e => e.Index))
            apply();

        return recorded;
    }

    private bool Record(string name, int arity, int position, string path)
    {
        var key = Key(name, arity);
        if (_ambiguous.Contains(key))
            return false;

        if (!_arguments.TryGetValue(key, out var byPosition))
            _arguments[key] = byPosition = [];

        if (!byPosition.TryGetValue(position, out var existing))
        {
            byPosition[position] = path;
            return true;
        }

        if (existing == path)
            return false;

        // The same registrar reached with two different tables. Documenting its members under
        // either one would be a guess, so it keeps none.
        _arguments.Remove(key);
        _ambiguous.Add(key);
        return true;
    }

    private static string Key(string name, int arity) => name + "/" + arity;

    /// <summary>The registrar functions defined in a file, in source order.</summary>
    public static IEnumerable<RegistrarDefinition> Definitions(SourceText source)
    {
        foreach (Match m in DefinitionPattern().Matches(source.Code))
        {
            var parameters = SolParser.SplitTopLevel(m.Groups["params"].Value)
                .Select(ParseParameter)
                .ToList();
            if (parameters is [{ Name: "" }])
                parameters.Clear();

            yield return new RegistrarDefinition(m.Index, m.Groups["name"].Value, parameters);
        }
    }

    private static RegistrarParameter ParseParameter(string text)
    {
        var declaration = text.Trim();
        var name = ParameterNamePattern().Match(declaration);
        return new RegistrarParameter(
            name.Success ? name.Groups["name"].Value : string.Empty,
            declaration.Contains("sol::table", StringComparison.Ordinal));
    }

    // ---- Patterns ----------------------------------------------------------

    /// <summary>A function definition: `void LuaBindings::Actors::RegisterTags(...) {`.</summary>
    [GeneratedRegex(@"\bvoid\s+(?:[A-Za-z_]\w*\s*::\s*)*(?<name>[A-Za-z_]\w*)\s*\((?<params>[^()]*)\)\s*\{")]
    private static partial Regex DefinitionPattern();

    /// <summary>A call statement whose arguments are all simple enough to match positionally.</summary>
    [GeneratedRegex(@"\b(?:[A-Za-z_]\w*\s*::\s*)*(?<name>[A-Za-z_]\w*)\s*\((?<args>[^()]*)\)\s*;")]
    private static partial Regex CallPattern();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"[\s&*](?<name>[A-Za-z_]\w*)\s*$")]
    private static partial Regex ParameterNamePattern();
}
