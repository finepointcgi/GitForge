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
    ILfsOperations Lfs { get; }

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

    /// <summary>Commits that touched <paramref name="path"/>, newest first, following renames.</summary>
    IAsyncEnumerable<FileHistoryEntry> GetFileHistoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Line-by-line authorship of <paramref name="path"/> at <paramref name="revision"/> (null = working tree).</summary>
    Task<BlameResult> GetBlameAsync(string path, string? revision, CancellationToken cancellationToken = default);

    /// <summary>Best common ancestor of two revisions (<c>git merge-base</c>), or null when they share no history.</summary>
    Task<string?> GetMergeBaseAsync(string a, string b, CancellationToken cancellationToken = default);

    /// <summary>Files changed from <paramref name="fromRevision"/> to <paramref name="toRevision"/>; null <paramref name="toRevision"/> means the working tree.</summary>
    Task<IReadOnlyList<FileChange>> GetChangesBetweenAsync(string fromRevision, string? toRevision, CancellationToken cancellationToken = default);

    /// <summary>Diff of one file from <paramref name="fromRevision"/> to <paramref name="toRevision"/> (null = working tree).</summary>
    Task<FileDiff?> GetDiffBetweenAsync(string fromRevision, string? toRevision, FileChange change, DiffOptions options, CancellationToken cancellationToken = default);
}

public interface IIndexOperations
{
    Task StageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);
    Task StageAllAsync(CancellationToken cancellationToken = default);
    Task UnstageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);
    Task UnstageAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards working-tree changes; untracked files are deleted. Destructive.</summary>
    Task DiscardAsync(IReadOnlyCollection<FileStatusEntry> files, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages, unstages, or discards a hunk/line selection via a generated partial patch. Works for new (untracked or
    /// added) files too: staging creates the index entry with only the selected lines; unstaging every line removes the
    /// entry; discarding every line of a new file leaves it empty (use <see cref="DiscardAsync"/> to delete it).
    /// </summary>
    Task ApplySelectionAsync(FileDiff diff, IReadOnlySet<LineRef> selection, PatchAction action, CancellationToken cancellationToken = default);

    Task<string> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);
    Task<string?> GetHeadMessageAsync(CancellationToken cancellationToken = default);

    Task ResolveConflictAsync(string path, ConflictSide side, CancellationToken cancellationToken = default);

    /// <summary>The base/ours/theirs versions of a conflicted file from index stages 1–3 (null when a side does not exist).</summary>
    Task<ConflictSides> GetConflictSidesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs the three-way merge with <c>git merge-file --diff3</c> and returns the text with conflict markers,
    /// or null when the file is binary or one side was deleted.
    /// </summary>
    Task<string?> GetMergeTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes the resolved content to the working tree and stages it (marks the conflict resolved).</summary>
    Task SaveResolutionAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>Resolves a modify/delete conflict: keep the file as it is in the working tree, or delete it.</summary>
    Task ResolveDeletionAsync(string path, bool keepFile, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Applies or pops a stash. Refuses when the working tree has conflicts in progress. When the stash touches files
    /// with uncommitted edits (which <c>git stash apply</c> refuses), its changes are applied as one atomic patch instead,
    /// so stashed lines can come back next to the lines that were kept.
    /// </summary>
    Task ApplyAsync(int index, bool pop, bool restoreIndex, CancellationToken cancellationToken = default);

    Task DropAsync(int index, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileChange>> GetChangesAsync(int index, CancellationToken cancellationToken = default);

    /// <summary>Files in the stash commit <paramref name="stashSha"/>: tracked changes, then untracked files it saved.</summary>
    Task<StashContents> GetContentsAsync(string stashSha, CancellationToken cancellationToken = default);

    /// <summary>Diff of one stashed file against the commit the stash was made on (untracked files show as added).</summary>
    Task<FileDiff?> GetDiffAsync(StashContents stash, StashFile file, DiffOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a hunk/line selection of one tracked file from the working tree into a new stash: the
    /// <c>git stash push --patch</c> equivalent. The stash's working tree is HEAD plus the selected lines; its index is the
    /// current index. Refuses files with staged changes (the selection would mix staged and unstaged content).
    /// </summary>
    Task SaveSelectionAsync(FileDiff diff, IReadOnlySet<LineRef> selection, string? message, CancellationToken cancellationToken = default);
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

/// <summary>Git LFS: tracked patterns, stored files, and server-side file locking (requires git-lfs on PATH).</summary>
public interface ILfsOperations
{
    /// <summary>True when the git-lfs extension is installed.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LfsPattern>> GetPatternsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LfsFile>> GetFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Server locks; uses <c>--verify</c> to tell yours from others', falling back to the plain list.</summary>
    Task<LfsLockList> GetLocksAsync(CancellationToken cancellationToken = default);

    Task<LfsLock> LockAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Releases a lock. <paramref name="force"/> breaks someone else's lock (requires server permission).</summary>
    Task UnlockAsync(string path, bool force, CancellationToken cancellationToken = default);

    Task TrackAsync(string pattern, bool lockable, CancellationToken cancellationToken = default);
}

public interface ISubmoduleOperations
{
    /// <summary><c>git submodule update [--init] [--recursive]</c> for every submodule.</summary>
    Task UpdateAsync(bool init, bool recursive, IProgress<string>? progress, CancellationToken cancellationToken = default);

    /// <summary>Same, limited to the given submodule paths (nested submodules follow with <paramref name="recursive"/>).</summary>
    Task UpdateAsync(IReadOnlyCollection<string> paths, bool init, bool recursive, IProgress<string>? progress, CancellationToken cancellationToken = default);

    /// <summary><c>git submodule sync</c>: copies URL changes from .gitmodules into the submodules' config.</summary>
    Task SyncAsync(bool recursive, CancellationToken cancellationToken = default);

    Task SyncAsync(IReadOnlyCollection<string> paths, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>Nested status of every submodule (<c>git submodule status --recursive</c>). Read-only.</summary>
    Task<IReadOnlyList<SubmoduleStatusEntry>> GetRecursiveStatusAsync(CancellationToken cancellationToken = default);
}
