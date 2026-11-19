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
    IWorktreeOperations Worktrees { get; }
    ITransferOperations Transfer { get; }

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

    /// <summary><c>git rm --cached</c>: stops tracking the files (the removal is staged) but leaves them on disk.</summary>
    Task StopTrackingAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Puts <paramref name="path"/> back to its content at <paramref name="revision"/>, staged (<c>git restore --source --staged --worktree</c>).</summary>
    Task RestoreFromAsync(string revision, string path, CancellationToken cancellationToken = default);
}

public interface IBranchOperations
{
    Task CheckoutAsync(string branchOrCommit, CancellationToken cancellationToken = default);
    Task CheckoutRemoteBranchAsync(string remoteBranch, string localName, CancellationToken cancellationToken = default);
    Task CreateAsync(string name, string startPoint, bool checkout, CancellationToken cancellationToken = default);
    Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, bool force, CancellationToken cancellationToken = default);

    /// <summary>Deletes a local branch only if it still points at <paramref name="expectedTip"/>, so commits made after a check are never lost.</summary>
    Task DeleteIfUnchangedAsync(string name, string expectedTip, CancellationToken cancellationToken = default);

    /// <summary>
    /// For cleanup: whether each branch's work is already in <paramref name="baseRef"/> (merged, or squash- or rebase-merged
    /// as other commits) or in HEAD. Changes no refs or working files.
    /// </summary>
    Task<IReadOnlyList<BranchMergeCheck>> CheckMergedAsync(IReadOnlyList<string> branches, string baseRef, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// <c>git bisect start</c>, then marks <paramref name="revision"/> good or bad. Once both a good and a bad commit are
    /// known, git checks out a commit halfway between them to test. Returns git's output.
    /// </summary>
    Task<GitResult> StartBisectAsync(string revision, BisectMark mark, CancellationToken cancellationToken = default);

    /// <summary><c>git bisect good|bad|skip [revision]</c> (null = the checked-out commit). Returns git's output.</summary>
    Task<GitResult> MarkBisectAsync(BisectMark mark, string? revision = null, CancellationToken cancellationToken = default);

    /// <summary><c>git bisect reset</c>: ends the session and returns to the branch it started on.</summary>
    Task ResetBisectAsync(CancellationToken cancellationToken = default);

    Task<BisectStatus> GetBisectStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Linked working trees (<c>git worktree</c>): more than one branch checked out at once, each in its own folder.</summary>
public interface IWorktreeOperations
{
    Task<IReadOnlyList<WorktreeInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>git worktree add</c>: checks out <paramref name="branch"/> in <paramref name="path"/>, or, with
    /// <paramref name="newBranch"/>, creates that branch at <paramref name="branch"/> (a start point) first.
    /// </summary>
    Task AddAsync(string path, string branch, string? newBranch, CancellationToken cancellationToken = default);

    /// <summary><c>git worktree remove</c>. Without <paramref name="force"/>, git refuses a worktree with uncommitted changes.</summary>
    Task RemoveAsync(string path, bool force, CancellationToken cancellationToken = default);

    /// <summary><c>git worktree prune</c>: forgets worktrees whose folders were deleted.</summary>
    Task PruneAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the worktree at <paramref name="path"/> has uncommitted changes or untracked files, including inside submodules.</summary>
    Task<bool> HasChangesAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Moving work without a network connection between the repositories: patches and bundles.</summary>
public interface ITransferOperations
{
    /// <summary><c>git format-patch</c>: one numbered .patch file per commit (oldest first) in <paramref name="directory"/>. Returns the files.</summary>
    Task<IReadOnlyList<string>> SaveCommitsAsPatchesAsync(IReadOnlyList<string> shasOldestFirst, string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes all uncommitted changes (staged, unstaged, and new files) to <paramref name="file"/> as one binary-safe patch,
    /// byte for byte as git produces it. Returns false (and writes nothing) when there are no changes.
    /// </summary>
    Task<bool> SaveUncommittedPatchAsync(string file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a patch file: a mailbox from <c>git format-patch</c> becomes commits (<c>git am -3</c>); a plain diff
    /// changes files (<c>git apply --3way</c>). Conflicts are left to resolve, as with a merge.
    /// </summary>
    Task<GitResult> ApplyPatchAsync(string file, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>git bundle create</c>: <paramref name="refs"/> (branch or tag names, or "--all") in one file, optionally only the
    /// commits after <paramref name="basis"/> (the receiving repository must already have it). Returns the refs it holds.
    /// </summary>
    Task<IReadOnlyList<string>> CreateBundleAsync(string file, IReadOnlyList<string> refs, string? basis, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a bundle, then fetches its branches as the remote <paramref name="remoteName"/> (added, or pointed at the
    /// file, as needed), so they show as <c>remoteName/branch</c> to check out or merge.
    /// </summary>
    Task FetchBundleAsync(string file, string remoteName, IProgress<string>? progress, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Updates one submodule at any depth (a path from <see cref="GetRecursiveStatusAsync"/>) to the commit its parent
    /// records, with its nested submodules. A nested submodule is updated from inside the submodule that contains it.
    /// </summary>
    Task UpdateOneAsync(string path, IProgress<string>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>git submodule update --remote</c>: moves top-level submodules to the newest commit of the branch they track
    /// (<c>submodule.&lt;name&gt;.branch</c>, default the remote's HEAD), then puts their nested submodules at the commits
    /// those new commits record. The superproject then shows the submodules as changed, ready to commit.
    /// </summary>
    Task UpdateToRemoteAsync(IReadOnlyCollection<string> paths, IProgress<string>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the submodule branch that holds the commit <paramref name="superRevision"/> records for <paramref name="path"/>
    /// (its current branch when that one does) to the branch's upstream, or to origin with tracking set. Unlike
    /// <c>git push --recurse-submodules=on-demand</c>, which pushes the submodule branch named like the superproject's,
    /// this pushes the branch the commit is actually on. Returns "remote/branch". Throws when no branch contains it.
    /// </summary>
    Task<string> PushRecordedCommitAsync(string path, string superRevision, IProgress<string>? progress, CancellationToken cancellationToken = default);

    /// <summary><c>git submodule add [-b branch] -- url path</c>: clones it and stages it with its .gitmodules entry.</summary>
    Task AddAsync(string url, string path, string? branch, IProgress<string>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>git submodule deinit -f</c> then <c>git rm -f</c>: deletes the submodule's folder and stages its removal (and its
    /// .gitmodules entry). Its git data stays in .git/modules, so commits made in it are not lost.
    /// </summary>
    Task RemoveAsync(string path, CancellationToken cancellationToken = default);
}
