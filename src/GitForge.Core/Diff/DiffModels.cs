namespace GitForge.Core.Diff;

public enum DiffLineKind
{
    Context,
    Added,
    Deleted,
}

/// <summary>One line of a hunk. <see cref="Text"/> keeps a trailing '\r' (CRLF files) so patches round-trip byte-exact.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber, bool NoNewlineAtEnd = false)
{
    public bool IsChange => Kind != DiffLineKind.Context;
    public string DisplayText => Text.EndsWith('\r') ? Text[..^1] : Text;
    public bool HasCarriageReturn => Text.EndsWith('\r');
}

public sealed record DiffHunk(int Index, int OldStart, int OldCount, int NewStart, int NewCount, string Section, IReadOnlyList<DiffLine> Lines)
{
    public string Header => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@{(Section.Length > 0 ? " " + Section : string.Empty)}";
    public bool HasChanges => Lines.Any(l => l.IsChange);
}

public sealed record FileDiff(
    string? OldPath,
    string? NewPath,
    IReadOnlyList<string> HeaderLines,
    IReadOnlyList<DiffHunk> Hunks,
    bool IsBinary)
{
    public string Path => NewPath ?? OldPath ?? string.Empty;
    public bool IsNewFile => OldPath is null;
    public bool IsDeletedFile => NewPath is null;
    public bool IsRename => OldPath is not null && NewPath is not null && OldPath != NewPath;
    public int Additions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
    public int Deletions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Deleted));
    public int LineCount => Hunks.Sum(h => h.Lines.Count + 1);

    /// <summary>Line- and hunk-level staging is supported for in-place text modifications only.</summary>
    public bool SupportsPartialStaging => !IsBinary && !IsNewFile && !IsDeletedFile && !IsRename && Hunks.Count > 0;
}

public sealed record DiffOptions(bool IgnoreWhitespace = false, int ContextLines = 3);

/// <summary>Address of a changed line inside a <see cref="FileDiff"/>.</summary>
public readonly record struct LineRef(int HunkIndex, int LineIndex);

/// <summary>What a partial patch does to the repository.</summary>
public enum PatchAction
{
    /// <summary>Apply selected working-tree changes to the index (<c>git apply --cached</c>).</summary>
    Stage,

    /// <summary>Remove selected changes from the index (<c>git apply --cached --reverse</c>).</summary>
    Unstage,

    /// <summary>Revert selected working-tree changes (<c>git apply --reverse</c>). Destructive.</summary>
    Discard,
}

/// <summary>Flattened row used by virtualized diff renderers.</summary>
public sealed record DiffRow(int RowIndex, int HunkIndex, int LineIndex, DiffHunk Hunk, DiffLine? Line)
{
    public bool IsHunkHeader => Line is null;
}

/// <summary>Row in side-by-side mode. Either side may be empty (padding).</summary>
public sealed record SideBySideRow(int RowIndex, int HunkIndex, DiffHunk Hunk, bool IsHunkHeader, DiffLine? Left, int LeftLineIndex, DiffLine? Right, int RightLineIndex);
