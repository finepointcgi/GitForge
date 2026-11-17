using System.Text;

namespace GitForge.Core.Merge;

public enum ConflictResolution
{
    Unresolved,
    Ours,
    Theirs,
    OursThenTheirs,
    TheirsThenOurs,
    Base,
    Custom,
}

public abstract class MergeSegment;

/// <summary>Lines both sides agree on (already merged by git).</summary>
public sealed class MergeCommon(IReadOnlyList<string> lines) : MergeSegment
{
    public IReadOnlyList<string> Lines { get; } = lines;
}

/// <summary>One conflicting region with the three versions and the user's choice.</summary>
public sealed class MergeConflict(int index, IReadOnlyList<string> ours, IReadOnlyList<string> @base, IReadOnlyList<string> theirs) : MergeSegment
{
    public int Index { get; } = index;
    public IReadOnlyList<string> Ours { get; } = ours;
    public IReadOnlyList<string> Base { get; } = @base;
    public IReadOnlyList<string> Theirs { get; } = theirs;
    public ConflictResolution Resolution { get; set; }

    /// <summary>Hand-edited result, used when <see cref="Resolution"/> is <see cref="ConflictResolution.Custom"/>.</summary>
    public string? CustomText { get; set; }

    public bool IsResolved => Resolution != ConflictResolution.Unresolved;

    public IReadOnlyList<string> ResolvedLines => Resolution switch
    {
        ConflictResolution.Ours => Ours,
        ConflictResolution.Theirs => Theirs,
        ConflictResolution.OursThenTheirs => [.. Ours, .. Theirs],
        ConflictResolution.TheirsThenOurs => [.. Theirs, .. Ours],
        ConflictResolution.Base => Base,
        ConflictResolution.Custom => SplitLines(CustomText ?? string.Empty),
        _ => throw new InvalidOperationException($"Conflict {Index + 1} is not resolved."),
    };

    /// <summary>Text to seed the manual editor with: the current choice, or "mine" when nothing is chosen yet.</summary>
    public string EditSeed => string.Join('\n', Resolution is ConflictResolution.Unresolved or ConflictResolution.Custom
        ? (CustomText is null ? Ours : SplitLines(CustomText))
        : ResolvedLines);

    internal static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1); // trailing newline in the editor
        return lines;
    }
}

/// <summary>
/// A file being merged, parsed from <c>git merge-file -p --diff3</c> output (markers <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>,
/// <c>|||||||</c>, <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>). Line endings are normalized for editing and restored on render.
/// </summary>
public sealed class MergeDocument
{
    private const int MarkerSize = 7;

    private MergeDocument(IReadOnlyList<MergeSegment> segments, string newLine, bool endsWithNewline)
    {
        Segments = segments;
        NewLine = newLine;
        EndsWithNewline = endsWithNewline;
        Conflicts = segments.OfType<MergeConflict>().ToList();
    }

    public IReadOnlyList<MergeSegment> Segments { get; }
    public IReadOnlyList<MergeConflict> Conflicts { get; }
    public string NewLine { get; }
    public bool EndsWithNewline { get; }
    public int ResolvedCount => Conflicts.Count(c => c.IsResolved);
    public bool IsFullyResolved => Conflicts.All(c => c.IsResolved);

    public static MergeDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = text.EndsWith('\n');
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

        var segments = new List<MergeSegment>();
        var common = new List<string>();
        List<string>? ours = null, @base = null, theirs = null, current = null;
        var conflictIndex = 0;

        foreach (var line in lines)
        {
            if (current is null)
            {
                if (IsMarker(line, '<'))
                {
                    if (common.Count > 0) segments.Add(new MergeCommon(common.ToList()));
                    common.Clear();
                    ours = [];
                    @base = [];
                    theirs = [];
                    current = ours;
                }
                else
                {
                    common.Add(line);
                }

                continue;
            }

            if (current == ours && IsMarker(line, '|')) current = @base;
            else if ((current == ours || current == @base) && line == new string('=', MarkerSize)) current = theirs;
            else if (current == theirs && IsMarker(line, '>'))
            {
                segments.Add(new MergeConflict(conflictIndex++, ours!, @base!, theirs!));
                current = null;
            }
            else current.Add(line);
        }

        if (current is not null) throw new FormatException("Unterminated conflict marker.");
        if (common.Count > 0) segments.Add(new MergeCommon(common));
        return new MergeDocument(segments, newLine, endsWithNewline);
    }

    /// <summary>The resolved file content. Throws while any conflict is unresolved.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var segment in Segments)
        {
            var lines = segment switch
            {
                MergeCommon c => c.Lines,
                MergeConflict k => k.ResolvedLines,
                _ => [],
            };
            foreach (var l in lines)
            {
                if (!first) sb.Append(NewLine);
                sb.Append(l);
                first = false;
            }
        }

        if (EndsWithNewline && !first) sb.Append(NewLine);
        return sb.ToString();
    }

    public void ResolveAll(ConflictResolution resolution)
    {
        foreach (var c in Conflicts) c.Resolution = resolution;
    }

    // "<<<<<<< label" / "||||||| label" / ">>>>>>> label" — exactly MarkerSize characters, then end or a space.
    private static bool IsMarker(string line, char c) =>
        line.Length >= MarkerSize
        && line.AsSpan(0, MarkerSize).IndexOfAnyExcept(c) < 0
        && (line.Length == MarkerSize || line[MarkerSize] == ' ');
}
