using GitForge.Core.Abstractions;
using GitForge.Core.Graph;
using GitForge.Core.Models;
using GitForge.Git.Undo;

namespace GitForge.UI.Services;

/// <summary>
/// Everything that belongs to one open repository (one tab): git handle, snapshot, graph rows, selection,
/// undo history, ref filters, and refresh bookkeeping. <see cref="RepoWorkspace"/> exposes the active session.
/// </summary>
public sealed class RepoSession : IAsyncDisposable
{
    public RepoSession(IGitRepository repo) => Repo = repo;

    public IGitRepository Repo { get; }
    public string Name => Repo.Name;
    public string Path => Repo.WorkingDirectory;

    public RepositorySnapshot? Snapshot { get; internal set; }
    public IReadOnlyList<SubmoduleStatusEntry> SubmoduleStatus { get; internal set; } = [];

    /// <summary>Linked worktrees of this repository (empty when it has none besides this one).</summary>
    public IReadOnlyList<WorktreeInfo> Worktrees { get; internal set; } = [];

    public BisectStatus Bisect { get; internal set; } = BisectStatus.None;

    /// <summary>The first line of git's answer to the last bisect step ("Bisecting: 6 revisions left…").</summary>
    public string? BisectMessage { get; internal set; }
    public IReadOnlyDictionary<string, IReadOnlyList<RefLabel>> RefIndex { get; internal set; } = new Dictionary<string, IReadOnlyList<RefLabel>>();
    public WorkingCopyStatus Status { get; internal set; } = WorkingCopyStatus.Empty;
    public AppendOnlyList<GraphRow> Rows { get; internal set; } = new();
    public bool HistoryLoading { get; internal set; }
    public UndoManager Undo { get; } = new();
    public List<string> ConsoleLines { get; } = [];

    // Selection and center pane.
    public bool WipSelected { get; internal set; }
    public string? SelectedCommitSha { get; internal set; }
    public CenterView Center { get; internal set; } = CenterView.Graph;
    public DiffTarget? OpenDiff { get; internal set; }
    public FileViewTarget? OpenFileView { get; internal set; }
    public BranchCompareTarget? BranchCompare { get; internal set; }
    public CommitCompareTarget? CommitCompare { get; internal set; }
    public string? OpenStashSha { get; internal set; }

    /// <summary>Commits picked with Ctrl+click or Shift+click in the graph (besides <see cref="SelectedCommitSha"/>).</summary>
    public HashSet<string> MultiSelection { get; } = new(StringComparer.Ordinal);
    public bool PanelFocused { get; set; }

    // Git LFS: tracked patterns/files and server locks (looked up by path).
    public LfsState Lfs { get; internal set; } = LfsState.None;
    internal HashSet<string> LfsPaths { get; set; } = new(StringComparer.Ordinal);
    internal Dictionary<string, LfsLock> LocksByPath { get; set; } = new(StringComparer.Ordinal);

    // Graph ref filters.
    public HashSet<string> HiddenRefs { get; } = new(StringComparer.Ordinal);
    public string? SoloRef { get; internal set; }

    /// <summary>Sidebar folders the user opened or closed in this tab; folders not listed use their default.</summary>
    public Dictionary<string, bool> SidebarFolders { get; } = new(StringComparer.Ordinal);

    // Refresh bookkeeping.
    internal CancellationTokenSource? HistoryCts { get; set; }
    internal CancellationTokenSource? AutoFetchCts { get; set; }
    internal int Refreshing;
    internal readonly Lock RefreshSync = new();
    internal RepositoryChangeKind PendingRefresh;
    internal int SubmoduleStatusVersion; // newest background submodule-status request wins

    /// <summary>Changes that arrived while this tab was in the background; applied when it is activated.</summary>
    internal RepositoryChangeKind StaleKinds;

    public async ValueTask DisposeAsync()
    {
        AutoFetchCts?.Cancel();
        HistoryCts?.Cancel();
        AutoFetchCts?.Dispose();
        HistoryCts?.Dispose();
        await Repo.DisposeAsync();
    }
}
