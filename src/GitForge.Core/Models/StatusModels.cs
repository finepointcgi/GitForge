namespace GitForge.Core.Models;

public enum FileChangeKind
{
    Unmodified,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Ignored,
    Conflicted,
}

public enum StagingArea
{
    /// <summary>Index → working tree.</summary>
    Unstaged,

    /// <summary>HEAD → index.</summary>
    Staged,
}

public sealed record FileStatusEntry(string Path, string? OldPath, FileChangeKind Kind, StagingArea Area)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string? Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/');
}

public sealed record WorkingCopyStatus(
    IReadOnlyList<FileStatusEntry> Staged,
    IReadOnlyList<FileStatusEntry> Unstaged,
    IReadOnlyList<FileStatusEntry> Conflicted)
{
    public static readonly WorkingCopyStatus Empty = new([], [], []);
    public bool IsClean => Staged.Count == 0 && Unstaged.Count == 0 && Conflicted.Count == 0;
    public int TotalCount => Staged.Count + Unstaged.Count + Conflicted.Count;
}

public sealed record CommitRequest(string Message, bool Amend = false, bool SignOff = false, bool AllowEmpty = false);

public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}

public enum ConflictSide
{
    Ours,
    Theirs,
}

public sealed record FetchRequest(string? Remote = null, bool AllRemotes = true, bool Prune = true, bool Tags = true);

/// <summary>Options for <c>git pull</c> (named to avoid confusion with hosting-provider pull requests).</summary>
public sealed record PullChangesRequest(string? Remote = null, string? Branch = null, bool Rebase = false, bool FastForwardOnly = false);

public sealed record PushRequest(
    string Remote,
    string LocalBranch,
    string? RemoteBranch = null,
    bool SetUpstream = false,
    bool ForceWithLease = false,
    bool IncludeTags = false);

public sealed record StashRequest(string? Message, bool IncludeUntracked = false, bool KeepIndex = false, IReadOnlyList<string>? Paths = null);
