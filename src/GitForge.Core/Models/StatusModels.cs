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

/// <summary>The three versions of a conflicted file (null = that side has no such file).</summary>
public sealed record ConflictSides(string? Base, string? Ours, string? Theirs, bool IsBinary)
{
    public bool IsDeleteConflict => !IsBinary && (Ours is null || Theirs is null);
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
    bool IncludeTags = false,
    SubmodulePushMode Submodules = SubmodulePushMode.Check);

/// <summary>What a push does about submodule commits that the pushed commits point to (<c>--recurse-submodules</c>).</summary>
public enum SubmodulePushMode
{
    /// <summary>Push without looking at submodules.</summary>
    None,

    /// <summary>Refuse the push when a submodule commit it points to is not on any of that submodule's remotes.</summary>
    Check,

    /// <summary>Push those submodule commits first, then the superproject.</summary>
    OnDemand,
}

public sealed record StashRequest(string? Message, bool IncludeUntracked = false, bool KeepIndex = false, IReadOnlyList<string>? Paths = null);

/// <summary>One file saved in a stash. <see cref="Untracked"/> files were saved with "include untracked files".</summary>
public sealed record StashFile(FileChange Change, bool Untracked);

/// <summary>
/// What a stash holds: <see cref="Sha"/> is the stash commit, <see cref="BaseSha"/> the commit it was made on, and
/// <see cref="UntrackedSha"/> the commit holding its untracked files (null when it has none).
/// </summary>
public sealed record StashContents(string Sha, string BaseSha, string? UntrackedSha, IReadOnlyList<StashFile> Files);
