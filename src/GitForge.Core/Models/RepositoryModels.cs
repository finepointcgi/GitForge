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
