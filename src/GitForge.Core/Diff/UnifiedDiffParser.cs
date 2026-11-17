using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitForge.Core.Diff;

/// <summary>
/// Parses <c>git diff</c> unified output. Splits on '\n' only, so CRLF content keeps its '\r' and
/// partial patches rebuilt from the model apply byte-exact.
/// </summary>
public static partial class UnifiedDiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeaderRegex();

    public static IReadOnlyList<FileDiff> Parse(string diffText)
    {
        ArgumentNullException.ThrowIfNull(diffText);
        var files = new List<FileDiff>();
        if (diffText.Length == 0) return files;

        var lines = diffText.Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0) count--; // trailing newline

        var i = 0;
        while (i < count)
        {
            if (!lines[i].StartsWith("diff ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            files.Add(ParseFile(lines, count, ref i));
        }

        return files;
    }

    public static FileDiff? ParseSingle(string diffText) => Parse(diffText) is [var first, ..] ? first : null;

    private static FileDiff ParseFile(string[] lines, int count, ref int i)
    {
        var header = new List<string> { lines[i] };
        var diffGitLine = lines[i];
        i++;

        string? minus = null, plus = null, renameFrom = null, renameTo = null;
        var isBinary = false;
        var isNew = false;
        var isDeleted = false;

        while (i < count && !lines[i].StartsWith("@@", StringComparison.Ordinal) && !lines[i].StartsWith("diff ", StringComparison.Ordinal))
        {
            var l = lines[i];
            header.Add(l);
            if (l.StartsWith("--- ", StringComparison.Ordinal)) minus = ParsePath(l[4..], "a/");
            else if (l.StartsWith("+++ ", StringComparison.Ordinal)) plus = ParsePath(l[4..], "b/");
            else if (l.StartsWith("rename from ", StringComparison.Ordinal)) renameFrom = Unquote(l["rename from ".Length..]);
            else if (l.StartsWith("rename to ", StringComparison.Ordinal)) renameTo = Unquote(l["rename to ".Length..]);
            else if (l.StartsWith("new file mode", StringComparison.Ordinal)) isNew = true;
            else if (l.StartsWith("deleted file mode", StringComparison.Ordinal)) isDeleted = true;
            else if (l.StartsWith("Binary files ", StringComparison.Ordinal) || l == "GIT binary patch") isBinary = true;
            i++;
        }

        // '/dev/null' sentinels arrive as empty strings from ParsePath.
        string? oldPath = minus ?? renameFrom;
        string? newPath = plus ?? renameTo;
        if (oldPath is null || newPath is null)
        {
            var (a, b) = ParseDiffGitLine(diffGitLine);
            oldPath ??= a;
            newPath ??= b;
        }

        if (isNew || oldPath == string.Empty) oldPath = null;
        if (isDeleted || newPath == string.Empty) newPath = null;

        var hunks = new List<DiffHunk>();
        while (i < count && lines[i].StartsWith("@@", StringComparison.Ordinal))
        {
            hunks.Add(ParseHunk(lines, count, ref i, hunks.Count));
        }

        // Skip anything else until the next file (e.g. binary patch payload).
        while (i < count && !lines[i].StartsWith("diff ", StringComparison.Ordinal)) i++;

        return new FileDiff(oldPath, newPath, header, hunks, isBinary);
    }

    private static DiffHunk ParseHunk(string[] lines, int count, ref int i, int index)
    {
        var m = HunkHeaderRegex().Match(lines[i]);
        if (!m.Success) throw new FormatException($"Malformed hunk header: '{lines[i]}'");

        var oldStart = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        var newStart = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
        var section = m.Groups[5].Value;
        i++;

        var result = new List<DiffLine>(oldCount + newCount);
        int oldLine = oldStart, newLine = newStart;
        int oldRemaining = oldCount, newRemaining = newCount;

        while (i < count && (oldRemaining > 0 || newRemaining > 0 || (lines[i].Length > 0 && lines[i][0] == '\\')))
        {
            var l = lines[i];
            if (l.Length == 0)
            {
                // Defensive: some tools strip the leading space from empty context lines.
                result.Add(new DiffLine(DiffLineKind.Context, string.Empty, oldLine++, newLine++));
                oldRemaining--;
                newRemaining--;
                i++;
                continue;
            }

            switch (l[0])
            {
                case ' ':
                    result.Add(new DiffLine(DiffLineKind.Context, l[1..], oldLine++, newLine++));
                    oldRemaining--;
                    newRemaining--;
                    break;
                case '-':
                    result.Add(new DiffLine(DiffLineKind.Deleted, l[1..], oldLine++, null));
                    oldRemaining--;
                    break;
                case '+':
                    result.Add(new DiffLine(DiffLineKind.Added, l[1..], null, newLine++));
                    newRemaining--;
                    break;
                case '\\':
                    if (result.Count > 0) result[^1] = result[^1] with { NoNewlineAtEnd = true };
                    break;
                default:
                    // Unexpected content: end the hunk to avoid misinterpreting the next file header.
                    return new DiffHunk(index, oldStart, oldCount, newStart, newCount, section, result);
            }

            i++;
        }

        return new DiffHunk(index, oldStart, oldCount, newStart, newCount, section, result);
    }

    private static string ParsePath(string raw, string prefix)
    {
        var tab = raw.IndexOf('\t');
        if (tab >= 0) raw = raw[..tab];
        raw = Unquote(raw);
        if (raw == "/dev/null") return string.Empty;
        return raw.StartsWith(prefix, StringComparison.Ordinal) ? raw[prefix.Length..] : raw;
    }

    private static (string? A, string? B) ParseDiffGitLine(string line)
    {
        // "diff --git a/foo b/foo" — best effort for mode-only or binary entries without ---/+++.
        const string marker = "diff --git ";
        if (!line.StartsWith(marker, StringComparison.Ordinal)) return (null, null);
        var rest = line[marker.Length..];
        var mid = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (rest.StartsWith("a/", StringComparison.Ordinal) && mid > 0)
        {
            return (rest[2..mid], rest[(mid + 3)..]);
        }

        return (null, null);
    }

    /// <summary>Decodes git's C-style quoted paths ("a/\303\251.txt").</summary>
    internal static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>(s.Length);
        for (var i = 1; i < s.Length - 1; i++)
        {
            var c = s[i];
            if (c != '\\')
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            var n = s[++i];
            switch (n)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                default:
                    if (n is >= '0' and <= '7' && i + 2 < s.Length - 1)
                    {
                        bytes.Add(Convert.ToByte(s.Substring(i, 3), 8));
                        i += 2;
                    }
                    else
                    {
                        bytes.Add((byte)n);
                    }

                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
