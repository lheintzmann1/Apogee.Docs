namespace Apogee.DocGen.Cpp;

using System.Text;
using System.Xml.Linq;

/// <summary>
/// The structured pieces Doxygen splits a documentation comment into.
///
/// Apogee's headers document with XML comments (<c>/// &lt;summary&gt;</c>, <c>&lt;param&gt;</c>,
/// <c>&lt;returns&gt;</c>). Doxygen folds <c>&lt;summary&gt;</c> into the brief description and
/// lifts the rest into `parameterlist` / `simplesect` nodes, so the same walker handles both the
/// XML-comment and the native `\param` styles.
/// </summary>
public sealed class ParsedDescription
{
    public string? Body { get; set; }
    public string? Returns { get; set; }
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.Ordinal);
    public List<string> SeeAlso { get; } = [];
    public string? Deprecated { get; set; }
    public string? Example { get; set; }
}

public static class DoxygenDescription
{
    public static ParsedDescription Parse(XElement? brief, XElement? detailed)
    {
        var result = new ParsedDescription();
        var body = new StringBuilder();

        Append(body, Render(brief, result));
        Append(body, Render(detailed, result));

        result.Body = Normalize(body.ToString());
        return result;
    }

    private static void Append(StringBuilder sb, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        if (sb.Length > 0)
            sb.Append("\n\n");
        sb.Append(text);
    }

    /// <summary>Renders a description element to markdown, siphoning off params/returns/see as it goes.</summary>
    private static string Render(XElement? element, ParsedDescription sink)
    {
        if (element is null)
            return string.Empty;
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
            RenderNode(node, sb, sink);
        return sb.ToString();
    }

    private static void RenderNode(XNode node, StringBuilder sb, ParsedDescription sink)
    {
        if (node is XText text)
        {
            sb.Append(text.Value);
            return;
        }
        if (node is not XElement e)
            return;

        switch (e.Name.LocalName)
        {
            case "para":
                var inner = new StringBuilder();
                foreach (var child in e.Nodes())
                    RenderNode(child, inner, sink);
                var paragraph = inner.ToString().Trim();
                if (paragraph.Length > 0)
                {
                    if (sb.Length > 0)
                        sb.Append("\n\n");
                    sb.Append(paragraph);
                }
                break;

            case "parameterlist":
                CollectParameters(e, sink);
                break;

            case "simplesect":
                CollectSimpleSect(e, sb, sink);
                break;

            case "xrefsect":
                // Doxygen wraps \deprecated and \todo in these.
                var title = e.Element("xreftitle")?.Value ?? string.Empty;
                var described = Render(e.Element("xrefdescription"), sink).Trim();
                if (title.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase))
                    sink.Deprecated = described.Length > 0 ? described : "Deprecated.";
                else if (described.Length > 0)
                    sb.Append("\n\n> [!NOTE]\n> ").Append(title).Append(": ").Append(described);
                break;

            case "ref":
                // Cross-references are emitted as DocFX xref links; unresolved uids degrade to plain text.
                var refId = (string?)e.Attribute("refid");
                var display = e.Value.Trim();
                if (refId is not null && display.Length > 0)
                    sb.Append('`').Append(display).Append('`');
                else
                    sb.Append(display);
                break;

            case "computeroutput":
                var code = e.Value;
                sb.Append('`').Append(code.Replace("`", "\\`")).Append('`');
                break;

            case "bold":
                sb.Append("**").Append(e.Value.Trim()).Append("**");
                break;

            case "emphasis":
                sb.Append('*').Append(e.Value.Trim()).Append('*');
                break;

            case "itemizedlist":
            case "orderedlist":
                var ordered = e.Name.LocalName == "orderedlist";
                var index = 1;
                sb.Append('\n');
                foreach (var li in e.Elements("listitem"))
                {
                    var itemText = Render(li, sink).Trim().Replace("\n", "\n  ");
                    sb.Append('\n').Append(ordered ? $"{index++}. " : "- ").Append(itemText);
                }
                sb.Append('\n');
                break;

            case "programlisting":
                sb.Append("\n\n```cpp\n");
                foreach (var line in e.Elements("codeline"))
                    sb.Append(line.Value).Append('\n');
                sb.Append("```\n");
                break;

            case "verbatim":
                sb.Append("\n\n```\n").Append(e.Value.Trim('\n')).Append("\n```\n");
                break;

            case "ulink":
                sb.Append('[').Append(e.Value).Append("](").Append((string?)e.Attribute("url")).Append(')');
                break;

            case "linebreak":
                sb.Append("  \n");
                break;

            case "sp":
                sb.Append(' ');
                break;

            case "ndash":
                sb.Append('–');
                break;

            case "mdash":
                sb.Append('—');
                break;

            case "nonbreakablespace":
                sb.Append(' ');
                break;

            case "parametername":
            case "parameterdescription":
                // Only reachable outside a parameterlist, which would be malformed; ignore.
                break;

            default:
                foreach (var child in e.Nodes())
                    RenderNode(child, sb, sink);
                break;
        }
    }

    private static void CollectParameters(XElement list, ParsedDescription sink)
    {
        var kind = (string?)list.Attribute("kind");
        foreach (var item in list.Elements("parameteritem"))
        {
            var description = Render(item.Element("parameterdescription"), sink).Trim();
            foreach (var nameElement in item.Descendants("parametername"))
            {
                var name = nameElement.Value.Trim();
                if (name.Length == 0)
                    continue;
                if (kind == "retval")
                {
                    sink.Returns = string.IsNullOrEmpty(sink.Returns)
                        ? $"`{name}` — {description}"
                        : sink.Returns + $"\n\n`{name}` — {description}";
                }
                else
                {
                    sink.Parameters[name] = description;
                }
            }
        }
    }

    private static void CollectSimpleSect(XElement section, StringBuilder sb, ParsedDescription sink)
    {
        var kind = (string?)section.Attribute("kind");
        var content = Render(section, sink).Trim();
        switch (kind)
        {
            case "return":
                sink.Returns = content;
                break;
            case "see":
                if (content.Length > 0)
                    sink.SeeAlso.Add(content);
                break;
            case "note":
            case "remark":
                Block(sb, "NOTE", content);
                break;
            case "warning":
            case "attention":
                Block(sb, "WARNING", content);
                break;
            case "since":
                if (content.Length > 0)
                    sb.Append("\n\nSince: ").Append(content);
                break;
            case "par":
                var title = section.Element("title")?.Value.Trim();
                if (title is { Length: > 0 })
                    sb.Append("\n\n**").Append(title).Append("**\n\n").Append(content);
                else if (content.Length > 0)
                    sb.Append("\n\n").Append(content);
                break;
            default:
                if (content.Length > 0)
                    sb.Append("\n\n").Append(content);
                break;
        }
    }

    private static void Block(StringBuilder sb, string kind, string content)
    {
        if (content.Length == 0)
            return;
        sb.Append("\n\n> [!").Append(kind).Append("]\n> ").Append(content.Replace("\n", "\n> "));
    }

    /// <summary>
    /// Collapses the whitespace Doxygen leaves behind. Line breaks inside a paragraph are folded
    /// because DocFX renders markdown, where a stray newline is meaningless but a doubled one is
    /// a paragraph break we must preserve.
    /// </summary>
    private static string? Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                blank++;
                continue;
            }
            if (sb.Length > 0)
                sb.Append(blank > 0 ? "\n\n" : "\n");
            blank = 0;
            sb.Append(line);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
