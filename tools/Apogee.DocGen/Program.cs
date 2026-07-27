using System.Text.Json;
using System.Text.Json.Serialization;
using Apogee.DocGen.Cpp;
using Apogee.DocGen.Lua;

namespace Apogee.DocGen;

/// <summary>Shape of docgen.json, which lives next to docfx.json in the docs repo.</summary>
public sealed class DocGenConfig
{
    /// <summary>Path to the engine checkout, relative to the config file.</summary>
    public string EngineRoot { get; set; } = "Apogee.Engine";

    /// <summary>Doxygen's XML output directory, relative to the config file.</summary>
    public string DoxygenXml { get; set; } = "obj/doxygen/xml";

    public CppConfig Cpp { get; set; } = new();
    public LuaConfig Lua { get; set; } = new();

    public sealed class CppConfig
    {
        public string Output { get; set; } = "api-cpp";
        public List<string> ExcludeTypes { get; set; } = [];
        public List<string> ExcludePaths { get; set; } = [];
        public bool IncludeProtected { get; set; } = true;
    }

    public sealed class LuaConfig
    {
        public string Output { get; set; } = "api-lua";

        /// <summary>Binding source folders, relative to the engine root.</summary>
        public List<string> Sources { get; set; } = ["Source/Engine/LuaScripting/Bindings"];

        /// <summary>Where to write the LuaCATS definitions, relative to the config file.</summary>
        public string? Definitions { get; set; } = "media/apogee.d.lua";

        public string RootVariable { get; set; } = "apogee";
        public string RootPath { get; set; } = "Apogee";

        /// <summary>Resolve member pointers against the C++ declarations from Doxygen.</summary>
        public bool UseNativeIndex { get; set; } = true;
    }
}

[JsonSerializable(typeof(DocGenConfig))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal sealed partial class ConfigContext : JsonSerializerContext;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0];
        var configPath = Path.GetFullPath(ArgumentValue(args, "--config") ?? "docgen.json");
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"error: config not found at '{configPath}'.");
            return 1;
        }

        var root = Path.GetDirectoryName(configPath)!;
        var config = JsonSerializer.Deserialize(File.ReadAllText(configPath), ConfigContext.Default.DocGenConfig);
        if (config is null)
        {
            Console.Error.WriteLine($"error: could not parse '{configPath}'.");
            return 1;
        }

        // The engine is usually reached through a symlink (build.sh points ./Apogee.Engine at a
        // sibling checkout). Doxygen records canonical paths, so resolve here too — otherwise the
        // two disagree and source paths never get shortened to repo-relative ones.
        var engineRoot = ResolveLink(Path.GetFullPath(Path.Combine(root, config.EngineRoot)));
        var doxygenXml = Path.GetFullPath(Path.Combine(root, config.DoxygenXml));

        try
        {
            return command switch
            {
                "cpp" => GenerateCpp(config, root, engineRoot, doxygenXml),
                "lua" => GenerateLua(config, root, engineRoot, doxygenXml),
                "all" => GenerateCpp(config, root, engineRoot, doxygenXml) is var cpp && cpp != 0
                    ? cpp
                    : GenerateLua(config, root, engineRoot, doxygenXml),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int GenerateCpp(DocGenConfig config, string root, string engineRoot, string doxygenXml)
    {
        var output = Path.GetFullPath(Path.Combine(root, config.Cpp.Output));
        Clean(output);

        var generator = new CppGenerator(new CppOptions
        {
            XmlDirectory = doxygenXml,
            OutputDirectory = output,
            EngineRoot = engineRoot,
            ExcludeTypes = config.Cpp.ExcludeTypes,
            ExcludePaths = config.Cpp.ExcludePaths,
            IncludeProtected = config.Cpp.IncludeProtected,
        });

        var count = generator.Generate();
        Console.WriteLine($"C++ API: {count} types -> {Relative(root, output)}");
        return 0;
    }

    private static int GenerateLua(DocGenConfig config, string root, string engineRoot, string doxygenXml)
    {
        var output = Path.GetFullPath(Path.Combine(root, config.Lua.Output));
        Clean(output);

        var native = config.Lua.UseNativeIndex && Directory.Exists(doxygenXml)
            ? NativeIndex.Load(doxygenXml)
            : NativeIndex.Empty;

        if (config.Lua.UseNativeIndex && !Directory.Exists(doxygenXml))
        {
            Console.WriteLine(
                "warning: Doxygen XML not found, so bindings that forward to a C++ member will be " +
                "documented without their signatures. Run the cpp step first.");
        }

        var files = new List<string>();
        foreach (var source in config.Lua.Sources)
        {
            var directory = Path.GetFullPath(Path.Combine(engineRoot, source));
            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"error: Lua binding source '{directory}' does not exist.");
                return 1;
            }
            files.AddRange(Directory.EnumerateFiles(directory, "*.cpp", SearchOption.AllDirectories));
        }

        var parser = new SolParser(new SolParserOptions
        {
            EngineRoot = engineRoot,
            RootVariable = config.Lua.RootVariable,
            RootPath = config.Lua.RootPath,
            Native = native,
        });

        var symbols = parser.Parse(files);

        var generator = new LuaGenerator(new LuaOptions
        {
            OutputDirectory = output,
            DefinitionsPath = config.Lua.Definitions is null
                ? null
                : Path.GetFullPath(Path.Combine(root, config.Lua.Definitions)),
        });
        generator.Generate(symbols);

        var members = symbols.Sum(s => s.Members.Count);
        Console.WriteLine($"Lua API: {symbols.Count} tables, {members} members " +
                          $"from {files.Count} binding files -> {Relative(root, output)}");
        if (config.Lua.Definitions is not null)
            Console.WriteLine($"Lua definitions: {config.Lua.Definitions}");

        foreach (var warning in parser.Warnings)
            Console.WriteLine($"warning: {warning}");

        return 0;
    }

    /// <summary>
    /// Empties the output folder so a type deleted from the engine cannot survive as a stale page.
    /// </summary>
    private static void Clean(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.yml"))
            File.Delete(file);
    }

    private static string ResolveLink(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'.");
        Usage();
        return 1;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            Apogee.DocGen — generates DocFX API metadata for the engine's C++ and Lua surfaces.

            Usage:
              docgen <command> [--config <path>]

            Commands:
              cpp     Convert Doxygen XML into DocFX pages for the C++ API.
              lua     Extract the Lua API from the sol2 bindings, and emit LuaCATS definitions.
              all     Run both, C++ first (the Lua step reuses its type information).

            Options:
              --config <path>   Path to docgen.json. Defaults to ./docgen.json.

            The C# API needs no step here: DocFX reads it from the built assembly and its XML
            documentation file, configured under "metadata" in docfx.json.
            """);
    }
}
