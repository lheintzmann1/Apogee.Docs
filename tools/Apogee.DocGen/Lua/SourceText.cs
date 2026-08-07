namespace Apogee.DocGen.Lua;

using System.Text;

/// <summary>
/// A C++ source file prepared for scanning: comments blanked out of the code (so a registration
/// call can never be matched inside a comment) while the comment text itself is kept, indexed by
/// line, so a declaration can pick up the prose written above it.
///
/// Only documentation comments — <c>///</c> and <c>/** */</c> — are kept. A plain <c>//</c> above
/// a registration is a note to whoever maintains the binding, and reading it as the entry's
/// description publishes half the reasoning in the file to people scripting a game.
/// </summary>
public sealed class SourceText
{
    public required string Path { get; init; }
    public required string Code { get; init; }

    private readonly List<int> _lineStarts = [];
    private readonly Dictionary<int, string> _commentByLine = new();
    private readonly HashSet<int> _noteLines = [];

    public static SourceText Load(string path)
    {
        var raw = File.ReadAllText(path).Replace("\r\n", "\n");
        var code = new StringBuilder(raw.Length);
        var comments = new Dictionary<int, string>();

        // Lines carrying a plain comment. Not documentation, but not a break in the block either:
        // a note written between a doc comment and the registration it describes is common enough
        // that it should not cost the entry its description.
        var notes = new HashSet<int>();

        var line = 1;
        var i = 0;

        // Whether code has already appeared on the current line. A comment that trails code
        // ("&VecT::Mod, // static") annotates that statement, not the one below it, so it must
        // not be picked up as the next declaration's description.
        var lineHasCode = false;

        while (i < raw.Length)
        {
            var c = raw[i];

            if (c == '\n')
            {
                code.Append('\n');
                line++;
                lineHasCode = false;
                i++;
                continue;
            }

            // String and character literals: copied verbatim so their contents stay matchable.
            if (c is '"' or '\'')
            {
                var quote = c;
                code.Append(c);
                i++;
                while (i < raw.Length)
                {
                    if (raw[i] == '\\' && i + 1 < raw.Length)
                    {
                        code.Append(raw[i]).Append(raw[i + 1]);
                        i += 2;
                        continue;
                    }
                    code.Append(raw[i]);
                    if (raw[i] == '\n')
                        line++;
                    if (raw[i] == quote)
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
            {
                var end = raw.IndexOf('\n', i);
                if (end < 0)
                    end = raw.Length;
                // "///", but not the "////////" of a rule.
                var isDocumentation = i + 2 < end && raw[i + 2] == '/' && (i + 3 >= end || raw[i + 3] != '/');
                if (!lineHasCode && isDocumentation)
                    comments[line] = raw[(i + 3)..end];
                else if (!lineHasCode)
                    notes.Add(line);
                code.Append(' ', end - i);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
            {
                var end = raw.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    end = raw.Length - 2;
                var block = raw[(i + 2)..end];
                // "/** ... */", the block form of "///".
                var isDocumentation = block.StartsWith('*') && block != "*";
                var blockLine = line;
                foreach (var commentLine in block.Split('\n'))
                {
                    if (blockLine != line || !lineHasCode)
                    {
                        if (isDocumentation)
                            comments[blockLine] = commentLine.TrimStart(' ', '\t', '*');
                        else
                            notes.Add(blockLine);
                    }
                    blockLine++;
                }
                foreach (var ch in raw[i..Math.Min(end + 2, raw.Length)])
                {
                    if (ch == '\n')
                    {
                        code.Append('\n');
                        line++;
                    }
                    else
                    {
                        code.Append(' ');
                    }
                }
                i = Math.Min(end + 2, raw.Length);
                continue;
            }

            code.Append(c);
            if (!char.IsWhiteSpace(c))
                lineHasCode = true;
            i++;
        }

        var source = new SourceText { Path = path, Code = code.ToString() };
        foreach (var (key, value) in comments)
            source._commentByLine[key] = value;
        source._noteLines.UnionWith(notes);

        source._lineStarts.Add(0);
        for (var k = 0; k < source.Code.Length; k++)
        {
            if (source.Code[k] == '\n')
                source._lineStarts.Add(k + 1);
        }

        return source;
    }

    public int LineOf(int index)
    {
        var low = 0;
        var high = _lineStarts.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_lineStarts[mid] <= index)
                low = mid;
            else
                high = mid - 1;
        }
        return low + 1;
    }

    /// <summary>
    /// The documentation in the contiguous comment block immediately above <paramref name="line"/>,
    /// in source order. A blank line ends the block, so unrelated prose further up is not absorbed.
    /// </summary>
    public IReadOnlyList<string> CommentAbove(int line)
    {
        var collected = new List<string>();
        for (var probe = line - 1; probe >= 1; probe--)
        {
            if (_noteLines.Contains(probe))
                continue;
            if (!_commentByLine.TryGetValue(probe, out var text))
                break;
            collected.Add(text);
        }
        collected.Reverse();
        return collected;
    }

    /// <summary>The comment block that opens the enclosing section, i.e. above a "---- Name ----" banner.</summary>
    public IReadOnlyList<string> CommentAtOrBelow(int line, int maxLines = 12)
    {
        var collected = new List<string>();
        for (var probe = line; probe < line + maxLines; probe++)
        {
            if (_noteLines.Contains(probe))
                continue;
            if (!_commentByLine.TryGetValue(probe, out var text))
                break;
            collected.Add(text);
        }
        return collected;
    }
}
