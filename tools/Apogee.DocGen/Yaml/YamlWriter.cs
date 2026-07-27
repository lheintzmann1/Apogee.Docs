namespace Apogee.DocGen.Yaml;

using System.Text;

/// <summary>
/// Minimal YAML emitter.
///
/// DocFX only ever reads these files back through its own ManagedReference schema, so the full
/// generality of a serializer buys nothing here — what matters is that key order stays stable
/// (the files are diffed between engine revisions) and that scalars are escaped predictably.
/// A hand-rolled writer gives both without a dependency, and without the attribute wrangling
/// needed to stop a general-purpose serializer from emitting nulls and reordering members.
/// </summary>
public sealed class YamlWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public override string ToString() => _sb.ToString();

    public void Raw(string line) => _sb.Append(line).Append('\n');

    public IDisposable Indent()
    {
        _indent++;
        return new Scope(this);
    }

    /// <summary>
    /// Opens a sequence entry ("- "), so that the first key written lands on the dash line.
    /// </summary>
    public IDisposable Item()
    {
        Pad();
        _sb.Append("- ");
        _indent++;
        _pendingDash = true;
        return new Scope(this);
    }

    private bool _pendingDash;

    public void Key(string key, string? value)
    {
        if (value is null)
            return;
        Pad();
        _sb.Append(key).Append(": ").Append(Scalar(value, _indent)).Append('\n');
    }

    /// <summary>Writes a value that must stay unquoted, such as an integer DocFX reads as a number.</summary>
    public void KeyLiteral(string key, string value)
    {
        Pad();
        _sb.Append(key).Append(": ").Append(value).Append('\n');
    }

    /// <summary>Writes a key whose value is a nested mapping or sequence.</summary>
    public IDisposable Section(string key)
    {
        Pad();
        _sb.Append(key).Append(":\n");
        _indent++;
        return new Scope(this);
    }

    public void List(string key, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
            return;
        using (Section(key))
        {
            foreach (var v in values)
            {
                Pad();
                _sb.Append("- ").Append(Scalar(v, _indent + 1)).Append('\n');
            }
        }
    }

    private void Pad()
    {
        if (_pendingDash)
        {
            // The "- " already supplied this level's indentation.
            _pendingDash = false;
            return;
        }
        _sb.Append(' ', _indent * 2);
    }

    /// <summary>
    /// Renders a scalar. Multi-line values become literal block scalars because summaries carry
    /// markdown (lists, fenced code) that folding would silently reflow into a single paragraph.
    /// </summary>
    private static string Scalar(string value, int indent)
    {
        if (value.Length == 0)
            return "''";

        if (value.Contains('\n'))
        {
            var pad = new string(' ', (indent + 1) * 2);
            var sb = new StringBuilder();
            // "|-" strips the trailing newline; without it every summary gains a blank line.
            sb.Append("|-\n");
            foreach (var line in value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            {
                if (line.Length == 0)
                    sb.Append('\n');
                else
                    sb.Append(pad).Append(line).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        var needsQuote = value.Length == 0
                         || char.IsWhiteSpace(value[0])
                         || char.IsWhiteSpace(value[^1])
                         || "!&*?|>%@`\"'{}[],#".Contains(value[0])
                         || value.StartsWith("- ", StringComparison.Ordinal)
                         || value.Contains(": ", StringComparison.Ordinal)
                         || value.EndsWith(':')
                         || IsAmbiguousPlain(value);

        if (!needsQuote)
            return value;

        return "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>Plain scalars that YAML would decode as something other than a string.</summary>
    private static bool IsAmbiguousPlain(string value) => value switch
    {
        "true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off" => true,
        _ => double.TryParse(value, out _),
    };

    private sealed class Scope(YamlWriter owner) : IDisposable
    {
        public void Dispose()
        {
            owner._indent--;
            owner._pendingDash = false;
        }
    }
}
