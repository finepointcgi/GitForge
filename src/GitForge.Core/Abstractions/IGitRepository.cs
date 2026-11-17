using GitForge.Core.Diff;
using GitForge.Core.Models;
using GitForge.Core.Rebase;

namespace GitForge.Core.Abstractions;

/// <summary>
/// An open repository. One instance per repository tab; owns a serialized libgit2 worker and a file-system watcher.
/// Reads use libgit2 in-process; writes and network operations use the git CLI so hooks, signing, LFS filters,
/// credential helpers (Git Credential Manager), proxies, and custom CAs behave exactly as on the command line.
/// </summary>
public interface IGitRepository : IAsyncDisposable
{
    string WorkingDirectory { get; }
    string Name { get; }

    /// <summary>Raised (debounced) when files, the index, refs, or config change on disk.</summary>
    event EventHandler<RepositoryChangedEventArgs>? Changed;

    IRepositoryReader Read { get; }
    IIndexOperations Index { get; }
    IBranchOperations Branches { get; }
    IRemoteOperations Remotes { get; }
    IStashOperations Stashes { get; }
    IHistoryOperations History { get; }
    ISubmoduleOperations Submodules { get; }

    /// <summary>Runs an arbitrary git command in this repository (integrated console). Treated as a write: caches refresh.</summary>
    Task<GitResult> RunRawAsync(IReadOnlyList<string> arguments, IProgress<string>? output = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a read-only git command (rev-parse, symbolic-ref…) without triggering a repository refresh.</summary>
    Task<GitResult> QueryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public interface IGitRepositoryFactory
{
    bool IsRepository(string path);
    string? Discover(string startPath);
    Task<IGitRepository> OpenAsync(string path, CancellationToken cancellationToken = default);
    Task<string> CloneAsync(string url, string destination, bool recurseSubmodules, IProgress<string>? progress, CancellationToken cancellationToken = default);
    Task<string> InitAsync(string path, string? initialBranch = "main", CancellationToken cancellationToken = default);
}

public interface IRepositoryReader
{
    Task<RepositorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<WorkingCopyStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<CommitInfo> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    Task<CommitDetails> GetCommitDetailsAsync(string sha, CancellationToken cancellationToken = default);
    Task<FileDiff?> GetWorkingDiffAsync(FileStatusEntry file, DiffOptions options, CancellationToken cancellationToken = default);
    Task<FileDiff?> GetCommitDiffAsync(string sha, FileChange change, DiffOptions options, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReflogEntryInfo>> GetReflogAsync(string refName = "HEAD", int max = 500, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetUnreachableCommitsAsync(CancellationToken cancellationToken = default);
}

public interface IIndexOperations
{
    Task StageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);
    Task StageAllAsync(CancellationToken cancellationToken = default);
    Task UnstageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);
    Task UnstageAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards working-tree changes; untracked files are deleted. Destructive.</summary>
    Task DiscardAsync(IReadOnlyCollection<FileStatusEntry> files, CancellationToken cancellationToken = default);

    /// <summary>Stages, unstages, or discards a hunk/line selection via a generated partial patch.</summary>
    Task ApplySelectionAsync(FileDiff diff, IReadOnlySet<LineRef> selection, PatchAction action, CancellationToken cancellationToken = default);

    Task<string> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);
    Task<string?> GetHeadMessageAsync(CancellationToken cancellationToken = default);

    Task ResolveConflictAsync(string path, ConflictSide side, CancellationToken cancellationToken = default);
    Task MarkResolvedAsync(string path, CancellationToken cancellationToken = default);
    Task<GitResult> LaunchMergeToolAsync(string path, string? tool, CancellationToken cancellationToken = default);
}

public interface IBranchOperations
{
    Task CheckoutAsync(string branchOrCommit, CancellationToken cancellationToken = default);
    Task CheckoutRemoteBranchAsync(string remoteBranch, string localName, CancellationToken cancellationToken = default);
    Task CreateAsync(string name, string startPoint, bool checkout, CancellationToken cancellationToken = default);
    Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, bool force, CancellationToken cancellationToken = default);
    Task DeleteRemoteAsync(string remote, string branch, IProgress<string>? progress, CancellationToken cancellationToken = default);
    Task SetUpstreamAsync(string localBranch, string? upstream, CancellationToken cancellationToken = default);
    Task CreateTagAsync(string name, string target, string? message, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(string name, CancellationToken cancellationToken = default);
}

public interface IRemoteOperations
{
    Task FetchAsync(FetchRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default);
    Task PullAsync(PullChangesRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default);
    Task PushAsync(PushRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default);
    Task AddAsync(string name, string url, CancellationToken cancellationToken = default);
    Task RemoveAsync(string name, CancellationToken cancellationToken = default);
    Task PruneAsync(string remote, IProgress<string>? progress, CancellationToken cancellationToken = default);
}

public interface IStashOperations
{
    Task SaveAsync(StashRequest request, CancellationToken cancellationToken = default);

    /// <summary>Applies or pops a stash. Refuses when the working tree has conflicts in progress.</summary>
    Task ApplyAsync(int index, bool pop, bool restoreIndex, CancellationToken cancellationToken = default);

    Task DropAsync(int index, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileChange>> GetChangesAsync(int index, CancellationToken cancellationToken = default);
}

public interface IHistoryOperations
{
    Task MergeAsync(string branch, bool noFastForward, CancellationToken cancellationToken = default);
    Task RebaseOntoAsync(string upstream, CancellationToken cancellationToken = default);
    Task CherryPickAsync(IReadOnlyList<string> shas, CancellationToken cancellationToken = default);
    Task RevertAsync(string sha, CancellationToken cancellationToken = default);
    Task ResetAsync(string target, ResetMode mode, CancellationToken cancellationToken = default);

    /// <summary>Builds the editable todo list for commits after <paramref name="baseSha"/> up to HEAD.</summary>
    Task<IReadOnlyList<RebaseTodoItem>> GetRebasePlanAsync(string baseSha, CancellationToken cancellationToken = default);

    /// <summary>Executes an interactive rebase non-interactively using the visual plan.</summary>
    Task<GitResult> RunInteractiveRebaseAsync(string baseSha, IReadOnlyList<RebaseTodoItem> plan, CancellationToken cancellationToken = default);

    Task<GitResult> ContinueAsync(CancellationToken cancellationToken = default);
    Task<GitResult> AbortAsync(CancellationToken cancellationToken = default);
    Task<GitResult> SkipAsync(CancellationToken cancellationToken = default);
}

public interface ISubmoduleOperations
{
    Task UpdateAsync(bool init, bool recursive, IProgress<string>? progress, CancellationToken cancellationToken = default);
    Task SyncAsync(bool recursive, CancellationToken cancellationToken = default);
}
