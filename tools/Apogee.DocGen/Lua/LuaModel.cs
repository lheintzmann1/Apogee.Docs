namespace Apogee.DocGen.Lua;

public enum LuaSymbolKind
{
    /// <summary>A plain table of functions and constants, e.g. <c>Apogee.Time</c>.</summary>
    Module,

    /// <summary>A sol2 usertype: a C++ class projected into Lua, e.g. <c>Apogee.Float3</c>.</summary>
    Class,

    /// <summary>A table of integer constants, from <c>new_enum</c> or a block of index assignments.</summary>
    Enum,
}

public enum LuaMemberKind
{
    Function,
    Method,
    Constructor,
    Operator,
    Field,
    Property,
    Constant,
    EnumValue,
}

public sealed class LuaParameter
{
    public required string Name { get; init; }
    public string Type { get; set; } = "any";
    public string? Description { get; set; }
    public bool Optional { get; set; }
}

public sealed class LuaMember
{
    public required string Name { get; init; }
    public required LuaMemberKind Kind { get; init; }
    public string? Description { get; set; }
    public string? Remarks { get; set; }
    public string? Example { get; set; }
    public List<LuaParameter> Parameters { get; } = [];
    public List<string> Returns { get; } = [];
    public string? ReturnDescription { get; set; }
    public string? ValueType { get; set; }
    public string? ConstantValue { get; set; }
    public bool ReadOnly { get; set; }
    public bool Deprecated { get; set; }
    public string? DeprecationMessage { get; set; }
    public List<string> SeeAlso { get; } = [];

    /// <summary>Extra signatures for a <c>sol::overload</c>, rendered under the primary one.</summary>
    public List<string> AdditionalSignatures { get; } = [];

    public string? SourceFile { get; set; }
    public int SourceLine { get; set; }

    /// <summary>True when the parameter list was inferred from a lambda rather than declared.</summary>
    public bool SignatureInferred { get; set; }
}

public sealed class LuaSymbol
{
    /// <summary>Dotted Lua path, e.g. <c>Apogee.Time</c> or <c>Apogee.Float3</c>.</summary>
    public required string Path { get; init; }

    public required LuaSymbolKind Kind { get; set; }
    public string? Description { get; set; }
    public string? Remarks { get; set; }
    public string? Example { get; set; }

    /// <summary>The C++ type a usertype projects, for the "backed by" note on the page.</summary>
    public string? NativeType { get; set; }

    public string? SourceFile { get; set; }
    public int SourceLine { get; set; }
    public List<LuaMember> Members { get; } = [];
    public List<string> SeeAlso { get; } = [];

    public string Name => Path.Contains('.') ? Path[(Path.LastIndexOf('.') + 1)..] : Path;
    public string? Parent => Path.Contains('.') ? Path[..Path.LastIndexOf('.')] : null;

    /// <summary>The binding module (Core, Math, Physics...), taken from the source folder.</summary>
    public string Group { get; set; } = "Engine";
}
