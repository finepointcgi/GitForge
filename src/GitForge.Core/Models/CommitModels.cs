namespace GitForge.Core.Models;

/// <summary>Lightweight commit record streamed from history. Kept small: 100k+ of these live in memory.</summary>
public sealed record CommitInfo(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Subject)
{
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
    public bool IsMerge => ParentShas.Count > 1;
    public bool IsRoot => ParentShas.Count == 0;
}

/// <summary>Full commit details, loaded on demand when a commit is selected.</summary>
public sealed record CommitDetails(
    CommitInfo Summary,
    string FullMessage,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommitDate,
    IReadOnlyList<FileChange> Changes);

/// <summary>A file touched by a commit.</summary>
public sealed record FileChange(string Path, string? OldPath, FileChangeKind Kind);

/// <summary>Query used for history loading. Path or text filters disable graph lanes (history is no longer contiguous).</summary>
public sealed record HistoryQuery
{
    public bool AllRefs { get; init; } = true;
    public string? StartRef { get; init; }

    /// <summary>Explicit tips to walk (hide/solo branches). Overrides <see cref="AllRefs"/> when non-empty.</summary>
    public IReadOnlyList<string>? Refs { get; init; }
    public string? PathFilter { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public int? MaxCount { get; init; }
    public bool IsFiltered => !string.IsNullOrWhiteSpace(PathFilter) || Since is not null || Until is not null;
}

/// <summary>One commit in a file's history; <see cref="Path"/> is the file's name in that commit (renames are followed).</summary>
public sealed record FileHistoryEntry(CommitInfo Commit, string Path, string? OldPath, FileChangeKind Kind)
{
    public FileChange ToChange() => new(Path, OldPath, Kind);
}

public sealed record BlameCommit(string Sha, string Author, string AuthorEmail, DateTimeOffset When, string Summary)
{
    /// <summary>Lines not yet committed are attributed to the all-zero SHA.</summary>
    public bool IsUncommitted => Sha.All(c => c == '0');
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
}

public sealed record BlameLine(int LineNumber, string Sha, string Text);

public sealed record BlameResult(IReadOnlyList<BlameLine> Lines, IReadOnlyDictionary<string, BlameCommit> Commits)
{
    public static readonly BlameResult Empty = new([], new Dictionary<string, BlameCommit>());
}

public sealed record ReflogEntryInfo(int Index, string FromSha, string ToSha, string Message, string Committer, DateTimeOffset When)
{
    public string Selector(string refName) => $"{refName}@{{{Index}}}";
}

/// <summary>Kinds of decoration drawn next to a commit in the graph.</summary>
public enum RefKind
{
    Head,
    LocalBranch,
    RemoteBranch,
    Tag,
    Stash,
}

public sealed record RefLabel(string Name, RefKind Kind, bool IsCurrent = false);
