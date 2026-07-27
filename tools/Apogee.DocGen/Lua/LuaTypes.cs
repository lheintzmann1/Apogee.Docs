namespace Apogee.DocGen.Lua;

using System.Text.RegularExpressions;

/// <summary>Maps the C++ types that appear in bindings onto the names a Lua author would recognise.</summary>
public static class LuaTypes
{
    private static readonly Dictionary<string, string> Direct = new(StringComparer.Ordinal)
    {
        ["bool"] = "boolean",
        ["void"] = "nil",
        ["float"] = "number",
        ["double"] = "number",
        ["int8"] = "integer",
        ["int16"] = "integer",
        ["int32"] = "integer",
        ["int64"] = "integer",
        ["uint8"] = "integer",
        ["uint16"] = "integer",
        ["uint32"] = "integer",
        ["uint64"] = "integer",
        ["byte"] = "integer",
        ["char"] = "integer",
        ["short"] = "integer",
        ["int"] = "integer",
        ["long"] = "integer",
        ["unsigned"] = "integer",
        ["size_t"] = "integer",
        ["intptr"] = "integer",
        ["uintptr"] = "integer",
        ["String"] = "string",
        ["StringView"] = "string",
        ["StringAnsi"] = "string",
        ["StringAnsiView"] = "string",
        ["Char"] = "string",
        ["Guid"] = "string",
        ["sol::table"] = "table",
        ["sol::function"] = "function",
        ["sol::protected_function"] = "function",
        ["sol::safe_function"] = "function",
        ["sol::object"] = "any",
        ["sol::variadic_args"] = "any",
        ["sol::nil_t"] = "nil",
        ["sol::this_state"] = "nil",
        ["std::string"] = "string",
        ["std::string_view"] = "string",
    };

    /// <summary>Normalises a C++ declaration fragment to a Lua-facing type name.</summary>
    public static string Map(string? cppType)
    {
        if (string.IsNullOrWhiteSpace(cppType))
            return "any";

        var text = cppType.Trim();
        text = Regex.Replace(text, @"\b(const|volatile|constexpr|struct|class|typename|inline)\b", " ");
        text = text.Replace("&&", " ").Replace("&", " ").Replace("*", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (text.Length == 0)
            return "any";

        // Container types read better as a Lua array of the element type.
        var array = Regex.Match(text, @"^(?:Array|Span|std::vector)\s*<\s*(?<element>[^,>]+)");
        if (array.Success)
            return Map(array.Groups["element"].Value) + "[]";

        var dictionary = Regex.Match(text, @"^(?:Dictionary|std::map|std::unordered_map)\s*<\s*(?<key>[^,]+),\s*(?<value>[^>]+)");
        if (dictionary.Success)
            return $"table<{Map(dictionary.Groups["key"].Value)}, {Map(dictionary.Groups["value"].Value)}>";

        var optional = Regex.Match(text, @"^(?:sol::optional|std::optional)\s*<\s*(?<inner>[^>]+)");
        if (optional.Success)
            return Map(optional.Groups["inner"].Value) + "?";

        if (Direct.TryGetValue(text, out var mapped))
            return mapped;

        // Drop any remaining template arguments: Vector3Base<float> is exposed as Float3 anyway,
        // and a raw template name is more useful than a mangled one.
        var angle = text.IndexOf('<');
        if (angle > 0)
        {
            var bare = text[..angle].Trim();
            if (Direct.TryGetValue(bare, out var mappedBare))
                return mappedBare;
            text = bare;
        }

        var scope = text.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0)
            text = text[(scope + 2)..];

        return text.Length == 0 ? "any" : text;
    }

    /// <summary>The Lua metamethod behind a <c>sol::meta_function</c> enumerator.</summary>
    public static string? MetaFunction(string enumerator) => enumerator switch
    {
        "addition" => "__add",
        "subtraction" => "__sub",
        "multiplication" => "__mul",
        "division" => "__div",
        "modulus" => "__mod",
        "power_of" => "__pow",
        "unary_minus" => "__unm",
        "floor_division" => "__idiv",
        "bitwise_and" => "__band",
        "bitwise_or" => "__bor",
        "bitwise_xor" => "__bxor",
        "bitwise_not" => "__bnot",
        "bitwise_left_shift" => "__shl",
        "bitwise_right_shift" => "__shr",
        "concatenation" => "__concat",
        "length" => "__len",
        "equal_to" => "__eq",
        "less_than" => "__lt",
        "less_than_or_equal_to" => "__le",
        "index" => "__index",
        "new_index" => "__newindex",
        "call" or "call_function" => "__call",
        "to_string" => "__tostring",
        "pairs" => "__pairs",
        "garbage_collect" => "__gc",
        "construct" => "new",
        _ => null,
    };

    /// <summary>The operator a metamethod stands for, for display in the signature.</summary>
    public static string? OperatorSymbol(string metamethod) => metamethod switch
    {
        "__add" => "a + b",
        "__sub" => "a - b",
        "__mul" => "a * b",
        "__div" => "a / b",
        "__mod" => "a % b",
        "__pow" => "a ^ b",
        "__unm" => "-a",
        "__idiv" => "a // b",
        "__concat" => "a .. b",
        "__len" => "#a",
        "__eq" => "a == b",
        "__lt" => "a < b",
        "__le" => "a <= b",
        "__tostring" => "tostring(a)",
        "__call" => "a(...)",
        _ => null,
    };
}
