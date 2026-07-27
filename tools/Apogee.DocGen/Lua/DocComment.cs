namespace Apogee.DocGen.Lua;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// A comment block above a binding, split into prose and tags.
///
/// Two styles are accepted so that documenting a binding never requires reformatting the file:
/// plain prose (already present throughout the binding sources, and used as-is), and explicit
/// tags for anything the parser cannot infer — parameter types, return types, examples. Tags may
/// be written as <c>@param</c> or in LuaCATS form (<c>---@param</c>), since the latter is what
/// authors already know from Lua tooling.
/// </summary>
public sealed class DocComment
{
    public string? Summary { get; private set; }
    public string? Remarks { get; private set; }
    public string? Example { get; private set; }
    public string? Deprecated { get; private set; }
    public bool IsDeprecated { get; private set; }
    public string? ExplicitName { get; private set; }
    public bool Hidden { get; private set; }
    public List<(string Name, string Type, string? Description, bool Optional)> Parameters { get; } = [];
    public List<(string Type, string? Description)> Returns { get; } = [];
    public List<(string Name, string Type, string? Description)> Fields { get; } = [];
    public List<string> SeeAlso { get; } = [];
    public List<string> Overloads { get; } = [];

    private static readonly Regex TagPattern = new(@"^\s*(?:-{1,3})?@(?<tag>\w+)\b\s*(?<rest>.*)$", RegexOptions.Compiled);

    private static readonly Regex BannerPattern =
        new(@"^\s*(?:-{3,}|={3,})\s*[\w.:]*\s*(?:-{3,}|={3,})?\s*$", RegexOptions.Compiled);

    public static DocComment Parse(IReadOnlyList<string> lines)
    {
        var doc = new DocComment();
        var prose = new StringBuilder();
        var example = new StringBuilder();
        var inExample = false;

        foreach (var raw in lines)
        {
            // Leading '/' survives from '///' comments.
            var line = raw.TrimStart('/').TrimEnd();

            // Section banners ("---- Apogee.Time ----", "==========") are visual rules in the
            // source, not prose. The label in the middle is the table name, which the reader of
            // the generated page already has in the heading.
            if (BannerPattern.IsMatch(line))
                continue;

            var match = TagPattern.Match(line);
            if (!match.Success)
            {
                if (inExample)
                    example.Append(line.TrimStart().Length == 0 ? string.Empty : line.Trim()).Append('\n');
                else
                    prose.Append(line.Trim()).Append('\n');
                continue;
            }

            inExample = false;
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var rest = match.Groups["rest"].Value.Trim();

            switch (tag)
            {
                case "param":
                {
                    var parts = SplitWords(rest, 2);
                    if (parts.Count == 0)
                        break;
                    var name = parts[0];
                    var optional = name.EndsWith('?');
                    doc.Parameters.Add((
                        name.TrimEnd('?'),
                        parts.Count > 1 ? parts[1] : "any",
                        parts.Count > 2 ? CleanDescription(parts[2]) : null,
                        optional));
                    break;
                }
                case "return":
                case "returns":
                {
                    var parts = SplitWords(rest, 1);
                    if (parts.Count == 0)
                        break;
                    doc.Returns.Add((parts[0], parts.Count > 1 ? CleanDescription(parts[1]) : null));
                    break;
                }
                case "field":
                {
                    var parts = SplitWords(rest, 2);
                    if (parts.Count < 1)
                        break;
                    doc.Fields.Add((
                        parts[0],
                        parts.Count > 1 ? parts[1] : "any",
                        parts.Count > 2 ? CleanDescription(parts[2]) : null));
                    break;
                }
                case "see":
                    if (rest.Length > 0)
                        doc.SeeAlso.Add(rest);
                    break;
                case "overload":
                    if (rest.Length > 0)
                        doc.Overloads.Add(rest);
                    break;
                case "deprecated":
                    doc.IsDeprecated = true;
                    doc.Deprecated = rest.Length > 0 ? rest : "Deprecated.";
                    break;
                case "name":
                case "luaname":
                    doc.ExplicitName = rest;
                    break;
                case "hidden":
                case "private":
                case "nodoc":
                    doc.Hidden = true;
                    break;
                case "example":
                case "usage":
                    inExample = true;
                    if (rest.Length > 0)
                        example.Append(rest).Append('\n');
                    break;
                case "remarks":
                case "note":
                    doc.Remarks = string.IsNullOrEmpty(doc.Remarks) ? rest : doc.Remarks + "\n" + rest;
                    break;
            }
        }

        var text = Collapse(prose.ToString());
        if (text is not null)
        {
            // The first paragraph is the summary; the rest becomes remarks, which keeps the
            // member tables in the API pages scannable instead of wrapping five lines each.
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0)
            {
                doc.Summary = text;
            }
            else
            {
                doc.Summary = text[..split].Trim();
                var rest = text[(split + 2)..].Trim();
                doc.Remarks = string.IsNullOrEmpty(doc.Remarks) ? rest : rest + "\n\n" + doc.Remarks;
            }
        }

        if (example.Length > 0)
            doc.Example = "```lua\n" + example.ToString().Trim('\n') + "\n```";

        // A lone "// Fields" or "// Methods" above a registration groups the source, it does not
        // describe the member underneath. Descriptions are sentences; bare labels are not.
        if (doc.Remarks is null && doc.Summary is not null && IsSectionLabel(doc.Summary))
            doc.Summary = null;

        return doc;
    }

    private static bool IsSectionLabel(string text) =>
        !text.Contains('\n')
        && text.Length <= 32
        && !text.EndsWith('.')
        && !text.EndsWith(':')
        && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3
        && char.IsUpper(text[0]);

    /// <summary>Splits into at most <paramref name="count"/> leading words plus the remainder.</summary>
    private static List<string> SplitWords(string text, int count)
    {
        var parts = new List<string>();
        var index = 0;
        while (parts.Count < count + 1 && index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;
            if (index >= text.Length)
                break;
            if (parts.Count == count)
            {
                parts.Add(text[index..].Trim());
                break;
            }
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
                index++;
            parts.Add(text[start..index]);
        }
        return parts;
    }

    private static string? CleanDescription(string text)
    {
        var cleaned = text.TrimStart('-', '—', '–', ':', ' ').Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Joins wrapped comment lines into paragraphs. Hard-wrapped prose is the norm in the binding
    /// sources, so preserving the source line breaks would produce ragged output in HTML.
    /// </summary>
    private static string? Collapse(string text)
    {
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        var paragraph = new StringBuilder();

        void Flush()
        {
            if (paragraph.Length == 0)
                return;
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(paragraph.ToString().Trim());
            paragraph.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            // Keep list items and fenced code on their own lines.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("```", StringComparison.Ordinal)
                || Regex.IsMatch(trimmed, @"^\d+\. "))
            {
                Flush();
                sb.Append(sb.Length > 0 ? "\n" : string.Empty).Append(trimmed);
                continue;
            }
            if (paragraph.Length > 0)
                paragraph.Append(' ');
            paragraph.Append(trimmed);
        }
        Flush();

        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
