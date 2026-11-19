namespace GitForge.Core.Models;

public sealed record BranchHead(
    string Name,
    string CanonicalName,
    string? TipSha,
    bool IsRemote,
    bool IsCurrent,
    string? RemoteName,
    string? UpstreamName,
    int? Ahead,
    int? Behind)
{
    public bool HasUpstream => UpstreamName is not null;
    public bool IsUpstreamGone => UpstreamName is not null && Ahead is null && Behind is null;
}

/// <summary>Whether deleting a branch would lose work, judged against a base branch.</summary>
public enum BranchMergeState
{
    /// <summary>Its last commit is in the base or in HEAD (merged or fast-forwarded).</summary>
    Merged,

    /// <summary>Its changes are in the base as other commits (a squash or rebase merge).</summary>
    ChangesInBase,

    /// <summary>It has changes that are not in the base.</summary>
    Unmerged,
}

/// <summary>A branch checked before cleanup. <see cref="UnmergedCommits"/> counts its commits whose changes are not in the base.</summary>
public sealed record BranchMergeCheck(string Branch, string TipSha, BranchMergeState State, int UnmergedCommits);

public sealed record TagInfo(string Name, string TargetSha, bool IsAnnotated, string? Message);

public sealed record RemoteInfo(string Name, string FetchUrl, string? PushUrl, RemoteRepositoryRef? Hosted);

public sealed record StashInfo(int Index, string Sha, string Message, DateTimeOffset When)
{
    public string RefSpec => $"stash@{{{Index}}}";
}

[Flags]
public enum SubmoduleState
{
    None = 0,
    NotInitialized = 1,
    Modified = 2,
    OutOfSync = 4,
    WorkDirDirty = 8,
}

public sealed record SubmoduleInfo(string Name, string Path, string Url, string? HeadSha, string? WorkDirSha, SubmoduleState State);

public enum SubmoduleSyncState
{
    Current,
    NotInitialized,
    OutOfSync,
    Conflicted,
}

/// <summary>
/// One submodule, at any depth: <see cref="Sha"/> is its checked-out commit (or the recorded one when not initialized),
/// <see cref="Depth"/> is 0 for top-level submodules, and <see cref="Branch"/> is null when detached.
/// </summary>
public sealed record SubmoduleStatusEntry(string Path, string Sha, SubmoduleSyncState State, int Depth, string? Branch)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));
}

/// <summary>Outcome of an automatic submodule update.</summary>
public sealed record SubmoduleUpdateResult(IReadOnlyList<string> Updated, IReadOnlyList<string> SkippedDirty)
{
    public static readonly SubmoduleUpdateResult None = new([], []);
    public bool IsEmpty => Updated.Count == 0 && SkippedDirty.Count == 0;
}

/// <summary>
/// One working tree of the repository (<c>git worktree list</c>). The main worktree comes first; <see cref="Branch"/> is
/// null when its HEAD is detached.
/// </summary>
public sealed record WorktreeInfo(string Path, string? HeadSha, string? Branch, bool IsMain, bool IsCurrent, bool IsBare, string? LockReason, bool IsLocked, bool IsPrunable)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
}

/// <summary>A submodule's recorded commit moving from <see cref="Old"/> to <see cref="New"/> (null when added or removed); <see cref="Dirty"/> when it has uncommitted changes.</summary>
public sealed record SubmoduleChange(string? Old, string? New, bool Dirty);

public enum BisectMark
{
    Good,
    Bad,
    Skip,
}

/// <summary>
/// State of a <c>git bisect</c> session: the bad commit, the good ones, how many were skipped, the first bad commit once
/// found, and roughly how many commits are left to test.
/// </summary>
public sealed record BisectStatus(string? Bad, IReadOnlyList<string> Good, int Skipped, string? FirstBad, int? Remaining)
{
    public static readonly BisectStatus None = new(null, [], 0, null, null);

    /// <summary>Started with a bad commit; a commit that worked is still needed before testing starts.</summary>
    public bool NeedsGood => FirstBad is null && Bad is not null && Good.Count == 0;

    /// <summary>A bad commit is still needed (started from "good" first).</summary>
    public bool NeedsBad => FirstBad is null && Bad is null;

    /// <summary>About how many more test-and-mark steps remain (binary search).</summary>
    public int? StepsLeft => Remaining is { } n && n > 0 ? (int)Math.Ceiling(Math.Log2(n + 1)) : null;
}

/// <summary>Operation the repository is in the middle of (drives the conflict/continue banner).</summary>
public enum RepositoryOperation
{
    None,
    Merge,
    Rebase,
    RebaseInteractive,
    CherryPick,
    Revert,
    Bisect,
    ApplyMailbox,
}

/// <summary>Immutable snapshot of refs and metadata. Rebuilt on every ref change; cheap to diff in the UI.</summary>
public sealed record RepositorySnapshot(
    string WorkingDirectory,
    string Name,
    string? HeadBranch,
    string? HeadSha,
    bool IsDetached,
    bool IsBare,
    RepositoryOperation Operation,
    IReadOnlyList<BranchHead> LocalBranches,
    IReadOnlyList<BranchHead> RemoteBranches,
    IReadOnlyList<TagInfo> Tags,
    IReadOnlyList<RemoteInfo> Remotes,
    IReadOnlyList<StashInfo> Stashes,
    IReadOnlyList<SubmoduleInfo> Submodules)
{
    /// <summary>Builds the SHA → decorations index used by the history view.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RefLabel>> BuildRefIndex()
    {
        var map = new Dictionary<string, List<RefLabel>>(StringComparer.Ordinal);
        void Add(string? sha, RefLabel label)
        {
            if (sha is null) return;
            if (!map.TryGetValue(sha, out var list)) map[sha] = list = [];
            list.Add(label);
        }

        if (IsDetached) Add(HeadSha, new RefLabel("HEAD", RefKind.Head, true));
        foreach (var b in LocalBranches) Add(b.TipSha, new RefLabel(b.Name, RefKind.LocalBranch, b.IsCurrent));
        foreach (var b in RemoteBranches) Add(b.TipSha, new RefLabel(b.Name, RefKind.RemoteBranch));
        foreach (var t in Tags) Add(t.TargetSha, new RefLabel(t.Name, RefKind.Tag));
        foreach (var s in Stashes) Add(s.Sha, new RefLabel(s.RefSpec, RefKind.Stash));
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<RefLabel>)kv.Value, StringComparer.Ordinal);
    }
}

public sealed class RepositoryChangedEventArgs(RepositoryChangeKind kind) : EventArgs
{
    public RepositoryChangeKind Kind { get; } = kind;
}

[Flags]
public enum RepositoryChangeKind
{
    None = 0,
    WorkingTree = 1,
    Index = 2,
    Refs = 4,
    Config = 8,
    All = WorkingTree | Index | Refs | Config,
}
