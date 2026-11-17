using GitForge.Core.Abstractions;
using GitForge.Core.Graph;
using GitForge.Core.Models;
using GitForge.Git;
using GitForge.Git.Submodules;
using GitForge.Git.Undo;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MudBlazor;

namespace GitForge.UI.Services;

/// <summary>What the center pane shows. The graph is home; other views open over it (Esc returns).</summary>
public enum CenterView
{
    Graph,
    Diff,
    Reflog,
    FileHistory,
    Blame,
    BranchChanges,
}

public enum PullMode
{
    /// <summary>Fast-forward if possible, otherwise merge.</summary>
    MergeIfNeeded,
    FastForwardOnly,
    Rebase,
}

/// <summary>A file opened in the center diff pane: either a working-copy file or a file in a commit.</summary>
public sealed record DiffTarget(FileStatusEntry? Working, string? CommitSha, FileChange? CommitFile)
{
    public string Path => Working?.Path ?? CommitFile?.Path ?? string.Empty;
}

/// <summary>A file shown in the history or blame view, optionally as of a revision (null = working tree / HEAD).</summary>
public sealed record FileViewTarget(string Path, string? Revision = null);

/// <summary>Compare a branch with the branch it came from: <see cref="Head"/> is "HEAD" or a branch name; a null base picks a sensible default.</summary>
public sealed record BranchCompareTarget(string Head, string? Base, bool IncludeWorkingTree);

/// <summary>
/// Per-window state: the open repository tabs and the active one. Components bind to the active session through
/// the delegating properties below, subscribe to <see cref="Changed"/> (coarse refresh) and <see cref="HistoryChanged"/>
/// (graph rows appended or tab switched). All git work runs off the UI thread; background tabs refresh on activation.
/// </summary>
public sealed partial class RepoWorkspace : IAsyncDisposable
{
    private const int FirstHistoryBatch = 300;
    private const int HistoryBatch = 4000;

    private readonly IGitRepositoryFactory _factory;
    private readonly ISettingsStore _settings;
    private readonly IAccountService _accounts;
    private readonly GitEngineOptions _engine;
    private readonly ISnackbar _snackbar;
    private readonly ILogger<RepoWorkspace> _log;
    private readonly List<RepoSession> _sessions = [];
    private readonly UndoManager _noUndo = new();
    private readonly List<string> _noConsole = [];
    private readonly HashSet<string> _noRefs = [];
    private RepoSession? _active;

    public RepoWorkspace(IGitRepositoryFactory factory, ISettingsStore settings, IAccountService accounts, GitEngineOptions engine, ISnackbar snackbar, ILogger<RepoWorkspace> log)
    {
        _factory = factory;
        _settings = settings;
        _accounts = accounts;
        _engine = engine;
        _snackbar = snackbar;
        _log = log;
        _engine.SelfHostedResolver = _accounts.ResolveSelfHosted;
    }

    // ---------- Tabs ----------

    public IReadOnlyList<RepoSession> Sessions => _sessions;
    public RepoSession? Active => _active;

    // ---------- Active-session state (what components bind to) ----------

    public IGitRepository? Repo => _active?.Repo;
    public RepositorySnapshot? Snapshot => _active?.Snapshot;

    /// <summary>Nested submodule status (<c>git submodule status --recursive</c>), refreshed with refs.</summary>
    public IReadOnlyList<SubmoduleStatusEntry> SubmoduleStatus => _active?.SubmoduleStatus ?? [];
    public IReadOnlyDictionary<string, IReadOnlyList<RefLabel>> RefIndex => _active?.RefIndex ?? new Dictionary<string, IReadOnlyList<RefLabel>>();
    public WorkingCopyStatus Status => _active?.Status ?? WorkingCopyStatus.Empty;
    public AppendOnlyList<GraphRow> Rows => _active?.Rows ?? EmptyRows;
    public bool HistoryLoading => _active?.HistoryLoading ?? false;
    public UndoManager Undo => _active?.Undo ?? _noUndo;
    public List<string> ConsoleLines => _active?.ConsoleLines ?? _noConsole;
    public bool WipSelected => _active?.WipSelected ?? false;
    public string? SelectedCommitSha => _active?.SelectedCommitSha;
    public CenterView Center => _active?.Center ?? CenterView.Graph;
    public DiffTarget? OpenDiff => _active?.OpenDiff;
    public FileViewTarget? OpenFileView => _active?.OpenFileView;
    public HashSet<string> HiddenRefs => _active?.HiddenRefs ?? _noRefs;
    public string? SoloRef => _active?.SoloRef;
    public bool IsFilteringRefs => SoloRef is not null || HiddenRefs.Count > 0;

    /// <summary>Keyboard focus: graph (false) or commit panel (true). h/l switch; vim keys act on the focused side.</summary>
    public bool PanelFocused
    {
        get => _active?.PanelFocused ?? false;
        set
        {
            if (_active is not null) _active.PanelFocused = value;
        }
    }

    private static readonly AppendOnlyList<GraphRow> EmptyRows = new();

    public bool IsBusy => BusyText is not null;
    public string? BusyText { get; private set; }
    public string? ProgressText { get; private set; }
    public bool ConsoleOpen { get; set; }
    public AppSettings Settings => _settings.Current;

    /// <summary>Coarse change notification (snapshot, status, busy state, selection, tabs).</summary>
    public event Action? Changed;

    /// <summary>History rows were appended or replaced, or the active tab changed.</summary>
    public event Action? HistoryChanged;

    /// <summary>Ask the graph to scroll a commit into view.</summary>
    public event Action<string>? RevealRequested;

    public string? CurrentBranch => Snapshot?.HeadBranch;

    public BranchHead? CurrentBranchHead => Snapshot?.LocalBranches.FirstOrDefault(b => b.IsCurrent);

    public RemoteInfo? DefaultRemote =>
        Snapshot?.Remotes.FirstOrDefault(r => r.Name == CurrentBranchHead?.RemoteName)
        ?? Snapshot?.Remotes.FirstOrDefault(r => r.Name == "origin")
        ?? (Snapshot is { Remotes.Count: > 0 } s ? s.Remotes[0] : null);

    public bool HasWip => Repo is not null && !Status.IsClean;

    // ---------- Git LFS ----------

    public LfsState Lfs => _active?.Lfs ?? LfsState.None;

    public bool IsLfsPath(string path) => _active?.LfsPaths.Contains(path) == true;

    public LfsLock? LockFor(string path) => _active is { } s && s.LocksByPath.TryGetValue(path, out var l) ? l : null;

    /// <summary>
    /// Reloads LFS patterns and files (and optionally server locks). Skipped entirely for repositories whose root
    /// .gitattributes does not route anything through LFS, so non-LFS repos never spawn git-lfs.
    /// </summary>
    private async Task RefreshLfsAsync(RepoSession session, bool includeLocks)
    {
        try
        {
            var attributes = Path.Combine(session.Path, ".gitattributes");
            var usesLfs = File.Exists(attributes) && (await File.ReadAllTextAsync(attributes)).Contains("filter=lfs", StringComparison.Ordinal);
            if (!usesLfs && !session.Lfs.InUse) return;
            if (!await session.Repo.Lfs.IsAvailableAsync())
            {
                session.Lfs = new LfsState(false, [new LfsPattern("(patterns in .gitattributes)", false, ".gitattributes")], [],
                    new LfsLockList([], false, "git-lfs is not installed, so LFS files are checked out as small pointer files. Install Git LFS (bundled with Git for Windows) and reopen the repository."));
                if (session == _active) Notify();
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var patterns = await session.Repo.Lfs.GetPatternsAsync();
            var files = await session.Repo.Lfs.GetFilesAsync();
            var locks = includeLocks ? await session.Repo.Lfs.GetLocksAsync() : session.Lfs.Locks;
            StartupTrace.Step(includeLocks ? "lfs: files and server locks" : "lfs: files", started);
            session.Lfs = new LfsState(true, patterns, files, locks);
            session.LfsPaths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            session.LocksByPath = locks.Locks.GroupBy(l => l.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            if (session == _active) Notify();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRefreshFailed(_log, ex);
        }
    }

    public Task RefreshLocksAsync() =>
        _active is { } s ? RunAsync("Refreshing LFS locks", (_, _) => RefreshLfsAsync(s, includeLocks: true)) : Task.CompletedTask;

    public Task LockFileAsync(string path) => _active is { } s
        ? RunAsync($"Locking {path}", async (r, _) =>
        {
            await r.Lfs.LockAsync(path);
            await RefreshLfsAsync(s, includeLocks: true);
        }, $"Locked {path}")
        : Task.CompletedTask;

    public Task UnlockFileAsync(string path, bool force) => _active is { } s
        ? RunAsync(force ? $"Breaking lock on {path}" : $"Unlocking {path}", async (r, _) =>
        {
            await r.Lfs.UnlockAsync(path, force);
            await RefreshLfsAsync(s, includeLocks: true);
        }, $"Unlocked {path}")
        : Task.CompletedTask;

    public Task TrackWithLfsAsync(string pattern, bool lockable) => _active is { } s
        ? RunAsync($"Tracking {pattern} with LFS", async (r, _) =>
        {
            await r.Lfs.TrackAsync(pattern, lockable);
            await RefreshLfsAsync(s, includeLocks: false);
        }, $"{pattern} is now stored in LFS (commit .gitattributes to share it)")
        : Task.CompletedTask;

    // ---------- Selection & center pane ----------

    public void SelectWip()
    {
        if (_active is not { } s) return;
        s.WipSelected = true;
        s.SelectedCommitSha = null;
        Notify();
    }

    public void SelectCommit(string sha, bool reveal = false)
    {
        if (_active is not { } s) return;
        s.WipSelected = false;
        s.SelectedCommitSha = sha;
        Notify();
        if (reveal) RevealRequested?.Invoke(sha);
    }

    public void ShowGraph()
    {
        if (_active is not { } s) return;
        s.Center = CenterView.Graph;
        s.OpenDiff = null;
        Notify();
    }

    public void ShowReflog() => SetCenter(CenterView.Reflog);

    public void ShowDiff(DiffTarget target)
    {
        if (_active is not { } s) return;
        s.OpenDiff = target;
        s.Center = CenterView.Diff;
        Notify();
    }

    public BranchCompareTarget? BranchCompare => _active?.BranchCompare;

    /// <summary>
    /// Back to the graph with the current branch's commit selected and scrolled into view. (The WIP row is always the
    /// top row; on an unborn branch, which has no commit yet, it is selected instead.)
    /// </summary>
    public void GoToHead()
    {
        if (_active is not { } s) return;
        s.Center = CenterView.Graph;
        s.OpenDiff = null;
        if (s.Snapshot?.HeadSha is { } head)
        {
            s.WipSelected = false;
            s.SelectedCommitSha = head;
        }
        else if (HasWip)
        {
            s.WipSelected = true;
            s.SelectedCommitSha = null;
        }

        Notify();
        RevealRequested?.Invoke(s.SelectedCommitSha ?? string.Empty);
    }

    /// <summary>Shows everything a branch changes since it diverged from its base (default: the current branch, including uncommitted work).</summary>
    public void ShowBranchChanges(string? head = null, string? baseRef = null)
    {
        if (_active is not { } s) return;
        var isCurrent = head is null || head == "HEAD" || head == CurrentBranch;
        s.BranchCompare = new BranchCompareTarget(isCurrent ? "HEAD" : head!, baseRef, IncludeWorkingTree: isCurrent);
        s.Center = CenterView.BranchChanges;
        Notify();
    }

    /// <summary>Deletes local branches whose upstream was deleted on the remote. Only fully merged branches are removed (<c>git branch -d</c>).</summary>
    public Task CleanUpGoneBranchesAsync(IReadOnlyList<string> names) =>
        RunAsync("Cleaning up branches", async (r, _) =>
        {
            var deleted = new List<string>();
            var unmerged = new List<string>();
            var failed = new List<string>();
            foreach (var name in names)
            {
                try
                {
                    await r.Branches.DeleteAsync(name, force: false); // git branch -d: refuses when commits would be lost
                    deleted.Add(name);
                }
                catch (GitCommandException ex) when (ex.GitMessage.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
                {
                    unmerged.Add(name);
                }
                catch (GitCommandException ex)
                {
                    failed.Add($"{name} ({ex.GitMessage.Split('\n')[0].Trim()})");
                }
            }

            if (deleted.Count > 0) Info($"Deleted {deleted.Count} branch{(deleted.Count == 1 ? string.Empty : "es")}: {string.Join(", ", deleted)}", Severity.Success);
            if (unmerged.Count > 0)
            {
                _snackbar.Add($"Kept {string.Join(", ", unmerged)}: {(unmerged.Count == 1 ? "it has" : "they have")} commits that are not in {CurrentBranch ?? "HEAD"} (common after squash merges). Right-click › Delete › Force delete if you are sure.",
                    Severity.Warning, o => o.VisibleStateDuration = 12000);
            }

            if (failed.Count > 0) _snackbar.Add($"Could not delete {string.Join(", ", failed)}", Severity.Error, o => o.VisibleStateDuration = 12000);
        });

    public void ShowFileHistory(string path) => ShowFileView(CenterView.FileHistory, new FileViewTarget(path));

    public void ShowBlame(string path, string? revision = null) => ShowFileView(CenterView.Blame, new FileViewTarget(path, revision));

    private void ShowFileView(CenterView view, FileViewTarget target)
    {
        if (_active is not { } s) return;
        s.OpenFileView = target;
        s.Center = view;
        Notify();
    }

    private void SetCenter(CenterView view)
    {
        if (_active is not { } s) return;
        s.Center = view;
        Notify();
    }

    // ---------- Lifecycle ----------

    public async Task InitializeAsync()
    {
        var s = await _settings.LoadAsync();
        _engine.GitExecutablePath = s.GitExecutablePath;
    }

    /// <summary>Re-opens the tabs from the last run, then activates <paramref name="openPath"/> (command line) or the last active tab.</summary>
    public async Task RestoreTabsAsync(string? openPath)
    {
        var saved = Settings.OpenRepositories.Where(Directory.Exists).ToList();
        if (saved.Count == 0 && openPath is null && Settings.RecentRepositories is [var recent, ..] && Directory.Exists(recent))
        {
            saved.Add(recent);
        }

        foreach (var path in saved)
        {
            await OpenSessionAsync(path, activate: false, report: false);
        }

        if (openPath is { Length: > 0 })
        {
            await OpenAsync(openPath);
        }
        else
        {
            var active = _sessions.FirstOrDefault(s => string.Equals(s.Path, Settings.ActiveRepository, StringComparison.OrdinalIgnoreCase)) ?? _sessions.LastOrDefault();
            if (active is not null) await ActivateAsync(active);
        }
    }

    /// <summary>Opens a repository in a new tab, or switches to its tab if it is already open.</summary>
    public async Task OpenAsync(string path)
    {
        var session = await OpenSessionAsync(path, activate: true, report: true);
        if (session is null) return;
        await _settings.UpdateAsync(s => s with { RecentRepositories = [session.Path, .. s.RecentRepositories.Where(r => !string.Equals(r, session.Path, StringComparison.OrdinalIgnoreCase)).Take(19)] });
    }

    private async Task<RepoSession?> OpenSessionAsync(string path, bool activate, bool report)
    {
        try
        {
            var root = _factory.Discover(path.Trim().Trim('"'));
            if (root is null)
            {
                if (report) _snackbar.Add($"Not a Git repository: {path}", Severity.Warning);
                return null;
            }

            var existing = _sessions.FirstOrDefault(s => string.Equals(Path.GetFullPath(s.Path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (activate) await ActivateAsync(existing);
                return existing;
            }

            var opening = Stopwatch.GetTimestamp();
            var repo = await _factory.OpenAsync(root);
            StartupTrace.Step($"repo: opened {repo.Name}", opening);
            var session = new RepoSession(repo) { StaleKinds = RepositoryChangeKind.All };
            repo.Changed += OnRepositoryChanged;
            session.Undo.Changed += (_, _) =>
            {
                if (session == _active) Notify();
            };
            _sessions.Add(session);
            if (activate) await ActivateAsync(session);
            await PersistTabsAsync();
            StartAutoFetch(session);
            return session;
        }
        catch (Exception ex)
        {
            if (report) Error("Open repository", ex);
            return null;
        }
    }

    /// <summary>Switches tabs; a background tab catches up on changes it missed.</summary>
    public async Task ActivateAsync(RepoSession session)
    {
        if (!_sessions.Contains(session)) return;
        var first = session.Snapshot is null;
        _active = session;
        Notify();
        HistoryChanged?.Invoke();

        var stale = session.StaleKinds;
        session.StaleKinds = RepositoryChangeKind.None;
        var refreshing = Stopwatch.GetTimestamp();
        if (stale != RepositoryChangeKind.None) await RefreshSessionAsync(session, stale);
        StartupTrace.Step("repo: refreshed on activation", refreshing);

        if (first)
        {
            if (HasWip) SelectWip();
            else if (Snapshot?.HeadSha is { } head) SelectCommit(head);
            var checking = Stopwatch.GetTimestamp();
            await CheckSubmodulesOnOpenAsync();
            StartupTrace.Step("repo: submodule check on open", checking);
            _ = RefreshLfsAsync(session, includeLocks: true); // network call for locks: don't block the tab
        }

        await PersistTabsAsync();
    }

    public Task ActivateRelativeAsync(int delta)
    {
        if (_sessions.Count < 2 || _active is null) return Task.CompletedTask;
        var i = _sessions.IndexOf(_active);
        return ActivateAsync(_sessions[(((i + delta) % _sessions.Count) + _sessions.Count) % _sessions.Count]);
    }

    public async Task CloseTabAsync(RepoSession session)
    {
        var index = _sessions.IndexOf(session);
        if (index < 0) return;
        session.Repo.Changed -= OnRepositoryChanged;
        _sessions.RemoveAt(index);
        await session.DisposeAsync();

        if (session == _active)
        {
            _active = null;
            if (_sessions.Count > 0)
            {
                await ActivateAsync(_sessions[Math.Min(index, _sessions.Count - 1)]);
            }
            else
            {
                Notify();
                HistoryChanged?.Invoke();
            }
        }
        else
        {
            Notify();
        }

        await PersistTabsAsync();
    }

    public Task CloseActiveTabAsync() => _active is null ? Task.CompletedTask : CloseTabAsync(_active);

    private Task PersistTabsAsync() =>
        _settings.UpdateAsync(s => s with { OpenRepositories = _sessions.Select(x => x.Path).ToList(), ActiveRepository = _active?.Path });

    /// <summary>Refreshes the active tab.</summary>
    public Task RefreshAsync(RepositoryChangeKind kind = RepositoryChangeKind.All) =>
        _active is null ? Task.CompletedTask : RefreshSessionAsync(_active, kind);

    /// <summary>Coalesces overlapping refresh requests: a running refresh picks up kinds queued while it works.</summary>
    private async Task RefreshSessionAsync(RepoSession session, RepositoryChangeKind kind)
    {
        lock (session.RefreshSync) session.PendingRefresh |= kind;
        if (Interlocked.Exchange(ref session.Refreshing, 1) == 1) return;
        try
        {
            while (true)
            {
                RepositoryChangeKind k;
                lock (session.RefreshSync)
                {
                    k = session.PendingRefresh;
                    session.PendingRefresh = RepositoryChangeKind.None;
                }

                if (k == RepositoryChangeKind.None || !_sessions.Contains(session)) break;
                var repo = session.Repo;

                var refs = (k & (RepositoryChangeKind.Refs | RepositoryChangeKind.Config)) != 0;
                if (refs)
                {
                    var step = Stopwatch.GetTimestamp();
                    var snapshot = await repo.Read.GetSnapshotAsync();
                    session.Snapshot = snapshot;
                    session.RefIndex = snapshot.BuildRefIndex();
                    StartupTrace.Step("repo: snapshot", step);
                    if (snapshot.Submodules.Count == 0) session.SubmoduleStatus = [];
                    else _ = RefreshSubmoduleStatusAsync(session);
                }

                var statusStep = Stopwatch.GetTimestamp();
                session.Status = await repo.Read.GetStatusAsync();
                StartupTrace.Step("repo: working tree status", statusStep);
                if (session.WipSelected && session.Status.IsClean && session.Snapshot?.HeadSha is { } head)
                {
                    session.WipSelected = false;
                    session.SelectedCommitSha = head;
                }

                if (session == _active) Notify();
                if (refs)
                {
                    _ = LoadHistoryAsync(session);
                    _ = RefreshLfsAsync(session, includeLocks: false); // commits/checkouts change which files are in LFS
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            LogRefreshFailed(_log, ex);
        }
        finally
        {
            Volatile.Write(ref session.Refreshing, 0);
        }

        bool again;
        lock (session.RefreshSync) again = session.PendingRefresh != RepositoryChangeKind.None;
        if (again) await RefreshSessionAsync(session, RepositoryChangeKind.None);
    }

    /// <summary>
    /// Recursive submodule state only feeds the sidebar, so it loads in the background and never holds up status or
    /// history. When refreshes overlap, only the newest result is applied.
    /// </summary>
    private async Task RefreshSubmoduleStatusAsync(RepoSession session)
    {
        var version = Interlocked.Increment(ref session.SubmoduleStatusVersion);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var status = await session.Repo.Submodules.GetRecursiveStatusAsync();
            StartupTrace.Step($"repo: submodule status, {status.Count} submodules (background)", started);
            if (Volatile.Read(ref session.SubmoduleStatusVersion) != version || !_sessions.Contains(session)) return;
            session.SubmoduleStatus = status;
            if (session == _active) Notify();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            LogRefreshFailed(_log, ex);
        }
    }

    /// <summary>Re-streams the active tab's history (after hide/solo changes).</summary>
    public Task LoadHistoryAsync() => _active is null ? Task.CompletedTask : LoadHistoryAsync(_active);

    /// <summary>Streams history through the incremental graph layout, publishing rows in batches.</summary>
    private async Task LoadHistoryAsync(RepoSession session)
    {
        session.HistoryCts?.Cancel();
        var cts = session.HistoryCts = new CancellationTokenSource();
        var rows = new AppendOnlyList<GraphRow>();
        var builder = new CommitGraphBuilder(GitForgeTheme.LaneColors.Length);
        var query = BuildHistoryQuery(session);
        session.HistoryLoading = true;
        var started = Stopwatch.GetTimestamp();

        try
        {
            await Task.Run(async () =>
            {
                var nextPublish = FirstHistoryBatch;
                await foreach (var commit in session.Repo.Read.GetHistoryAsync(query, cts.Token))
                {
                    rows.Add(builder.Add(commit));
                    if (rows.Count >= nextPublish)
                    {
                        if (nextPublish == FirstHistoryBatch) StartupTrace.Step("history: first rows shown", started);
                        Publish(session, rows, cts);
                        nextPublish = rows.Count + HistoryBatch;
                    }
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return; // tab closed while loading
        }
        catch (Exception ex)
        {
            Error("Load history", ex);
        }

        if (cts.IsCancellationRequested) return;
        session.HistoryLoading = false;
        StartupTrace.Step($"history: {rows.Count} commits loaded", started);
        Publish(session, rows, cts);
        if (session == _active) Notify();
    }

    private static HistoryQuery BuildHistoryQuery(RepoSession session)
    {
        if (session.SoloRef is { } solo) return new HistoryQuery { Refs = [solo] };
        if (session.HiddenRefs.Count == 0 || session.Snapshot is not { } s) return new HistoryQuery();

        // Tags are left out while hiding so they cannot pull hidden branches back into the graph.
        var refs = s.LocalBranches.Concat(s.RemoteBranches)
            .Select(b => b.CanonicalName)
            .Where(r => !session.HiddenRefs.Contains(r))
            .ToList();
        var current = s.LocalBranches.FirstOrDefault(b => b.IsCurrent);
        var currentHidden = current is not null && session.HiddenRefs.Contains(current.CanonicalName);
        if (!currentHidden && s.HeadSha is not null) refs.Add("HEAD");
        return refs.Count == 0 ? new HistoryQuery { Refs = ["HEAD"] } : new HistoryQuery { Refs = refs };
    }

    public Task ToggleHiddenAsync(string canonicalRef)
    {
        if (_active is not { } s) return Task.CompletedTask;
        if (!s.HiddenRefs.Remove(canonicalRef)) s.HiddenRefs.Add(canonicalRef);
        s.SoloRef = null;
        Notify();
        return LoadHistoryAsync(s);
    }

    public Task SoloAsync(string? canonicalRef)
    {
        if (_active is not { } s) return Task.CompletedTask;
        s.SoloRef = s.SoloRef == canonicalRef ? null : canonicalRef;
        s.HiddenRefs.Clear();
        Notify();
        return LoadHistoryAsync(s);
    }

    public Task ShowAllRefsAsync()
    {
        if (_active is not { } s) return Task.CompletedTask;
        s.HiddenRefs.Clear();
        s.SoloRef = null;
        Notify();
        return LoadHistoryAsync(s);
    }

    private void Publish(RepoSession session, AppendOnlyList<GraphRow> rows, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested) return;
        session.Rows = rows;
        if (session == _active) HistoryChanged?.Invoke();
    }

    // ---------- Operations ----------

    /// <summary>Runs a git operation on the active tab with busy state, progress text, and user-facing error reporting.</summary>
    public async Task<bool> RunAsync(string label, Func<IGitRepository, IProgress<string>, Task> operation, string? success = null)
    {
        var session = _active;
        if (session is null) return false;
        var repo = session.Repo;

        // Capture submodule pins so submodules can follow any HEAD move (checkout, pull, merge, rebase, reset, undo…).
        var beforeHead = session.Snapshot?.HeadSha;
        var beforePins = Settings.AutoUpdateSubmodules && session.Snapshot is { } snap ? SubmoduleAutoUpdater.Capture(snap) : null;

        BusyText = label;
        ProgressText = null;
        Notify();
        var progress = new Progress<string>(line =>
        {
            ProgressText = line;
            Notify();
        });

        try
        {
            await Task.Run(() => operation(repo, progress));
            if (success is not null) _snackbar.Add(success, Severity.Success);
            if (beforePins is not null) await FollowHeadWithSubmodulesAsync(repo, beforeHead, beforePins, progress);
            return true;
        }
        catch (Exception ex)
        {
            Error(label, ex);
            return false;
        }
        finally
        {
            BusyText = null;
            ProgressText = null;
            if (_sessions.Contains(session)) await RefreshSessionAsync(session, RepositoryChangeKind.All);
            else Notify();
        }
    }

    /// <summary>
    /// When HEAD moved, recursively updates submodules whose recorded commit changed (or that are new).
    /// A failure here is reported separately: the user's main operation already succeeded.
    /// </summary>
    private async Task FollowHeadWithSubmodulesAsync(IGitRepository repo, string? beforeHead, IReadOnlyDictionary<string, string?> before, IProgress<string> progress)
    {
        try
        {
            var head = (await repo.QueryAsync(["rev-parse", "--verify", "--quiet", "HEAD"])).StandardOutput.Trim();
            if (head.Length == 0 || head == beforeHead) return;
            if (before.Count == 0 && !File.Exists(Path.Combine(repo.WorkingDirectory, ".gitmodules"))) return;

            BusyText = "Updating submodules (recursive)";
            ProgressText = null;
            Notify();
            ReportSubmoduleUpdate(await Task.Run(() => SubmoduleAutoUpdater.UpdateChangedAsync(repo, before, progress)));
        }
        catch (Exception ex)
        {
            Error("Submodule update", ex);
        }
    }

    /// <summary>Explicit "Update submodules": every submodule to its recorded commit, recursively; dirty ones are skipped.</summary>
    public Task UpdateSubmodulesAsync() =>
        RunAsync("Updating submodules (recursive)", async (r, p) =>
        {
            var result = await SubmoduleAutoUpdater.UpdateAllAsync(r, p);
            ReportSubmoduleUpdate(result);
            if (result.IsEmpty) Info("No submodules to update.");
        });

    /// <summary>Updates one top-level submodule (and its nested submodules).</summary>
    public Task UpdateSubmoduleAsync(string path) =>
        RunAsync($"Updating {path}", (r, p) => r.Submodules.UpdateAsync([path], init: true, recursive: true, p), $"Updated {path}");

    private async Task CheckSubmodulesOnOpenAsync()
    {
        if (Repo is null || Snapshot is not { Submodules.Count: > 0 } s) return;

        // Initializing is always safe (nothing is checked out yet), so do it without asking.
        if (Settings.AutoUpdateSubmodules && s.Submodules.Any(m => m.HeadSha is not null && m.State.HasFlag(SubmoduleState.NotInitialized)))
        {
            await RunAsync("Initializing submodules (recursive)", async (r, p) => ReportSubmoduleUpdate(await SubmoduleAutoUpdater.InitializeMissingAsync(r, p)));
        }

        // Moving an existing checkout could surprise someone working inside a submodule, so offer it instead.
        var outOfSync = Snapshot is { } now ? SubmoduleAutoUpdater.OutOfSync(now) : [];
        if (outOfSync.Count > 0)
        {
            _snackbar.Add(
                $"{outOfSync.Count} submodule{(outOfSync.Count == 1 ? " is" : "s are")} not at the commit this branch records ({string.Join(", ", outOfSync.Take(3))}).",
                Severity.Warning,
                o =>
                {
                    o.Action = "Update";
                    o.ActionColor = Color.Primary;
                    o.VisibleStateDuration = 15000;
                    o.OnClick = _ => UpdateSubmodulesAsync();
                });
        }
    }

    private void ReportSubmoduleUpdate(SubmoduleUpdateResult result)
    {
        if (result.Updated.Count > 0)
        {
            Info($"Updated submodule{(result.Updated.Count == 1 ? string.Empty : "s")} (recursive): {string.Join(", ", result.Updated.Take(5))}{(result.Updated.Count > 5 ? "…" : string.Empty)}", Severity.Success);
        }

        if (result.SkippedDirty.Count > 0)
        {
            _snackbar.Add(
                $"Skipped submodule{(result.SkippedDirty.Count == 1 ? string.Empty : "s")} with local changes: {string.Join(", ", result.SkippedDirty)}. Commit or stash inside them, then update.",
                Severity.Warning, o => o.VisibleStateDuration = 12000);
        }
    }

    /// <summary>Runs an operation that records an undo entry (Ctrl+Z reverses it).</summary>
    public Task<bool> RunUndoableAsync(string label, Func<IGitRepository, Task<UndoEntry?>> operation, string? success = null)
    {
        var undo = Undo;
        return RunAsync(label, async (r, _) => undo.Record(await operation(r)), success);
    }

    public Task UndoAsync()
    {
        var undo = Undo;
        return !undo.CanUndo ? Task.CompletedTask
            : RunAsync($"Undoing {undo.UndoDescription}", async (r, _) => Info($"Undid {await undo.UndoAsync(r)}"));
    }

    public Task RedoAsync()
    {
        var undo = Undo;
        return !undo.CanRedo ? Task.CompletedTask
            : RunAsync($"Redoing {undo.RedoDescription}", async (r, _) => Info($"Redid {await undo.RedoAsync(r)}"));
    }

    public Task CheckoutAsync(string target) =>
        RunUndoableAsync($"Checking out {target}", r => UndoableOperations.CheckoutAsync(r, target));

    public Task FetchAsync() => RunAsync("Fetching", (r, p) => r.Remotes.FetchAsync(new FetchRequest(), p), "Fetch complete");

    public Task PullAsync(PullMode mode = PullMode.MergeIfNeeded) => RunAsync("Pulling", (r, p) => r.Remotes.PullAsync(
        new PullChangesRequest(Rebase: mode == PullMode.Rebase, FastForwardOnly: mode == PullMode.FastForwardOnly), p), "Pull complete");

    public Task PushAsync(bool force = false)
    {
        var head = CurrentBranchHead;
        var remote = DefaultRemote;
        if (head is null || remote is null)
        {
            _snackbar.Add(head is null ? "Check out a branch before pushing." : "This repository has no remotes.", Severity.Warning);
            return Task.CompletedTask;
        }

        var remoteBranch = head.UpstreamName is { } up && up.StartsWith(remote.Name + "/", StringComparison.Ordinal) ? up[(remote.Name.Length + 1)..] : head.Name;
        var request = new PushRequest(remote.Name, head.Name, remoteBranch, SetUpstream: !head.HasUpstream, ForceWithLease: force);
        return RunAsync("Pushing", (r, p) => r.Remotes.PushAsync(request, p), $"Pushed {head.Name} → {remote.Name}/{remoteBranch}");
    }

    public Task PopStashAsync()
    {
        if (Snapshot?.Stashes.Count is null or 0)
        {
            Info("There are no stashes to pop.", Severity.Warning);
            return Task.CompletedTask;
        }

        return RunAsync("Popping stash", (r, _) => r.Stashes.ApplyAsync(0, pop: true, restoreIndex: false), "Stash popped");
    }

    public async Task<GitResult?> RunConsoleAsync(IReadOnlyList<string> args)
    {
        var session = _active;
        if (session is null || args.Count == 0) return null;
        var lines = session.ConsoleLines;
        lines.Add("$ git " + string.Join(' ', args));
        BusyText = "git " + args[0];
        Notify();
        try
        {
            var result = await Task.Run(() => session.Repo.RunRawAsync(args));
            foreach (var l in (result.StandardOutput + result.StandardError).Split('\n')) lines.Add(l.TrimEnd('\r'));
            lines.Add($"[exit {result.ExitCode}]");
            if (lines.Count > 5000) lines.RemoveRange(0, lines.Count - 5000);
            return result;
        }
        catch (Exception ex)
        {
            lines.Add("error: " + ex.Message);
            return null;
        }
        finally
        {
            BusyText = null;
            Notify();
        }
    }

    public void Error(string operation, Exception ex)
    {
        LogOperationFailed(_log, ex, operation);
        var message = ex is GitCommandException g ? g.GitMessage : ex.Message;
        if (message.Length > 600) message = message[..600] + "…";
        _snackbar.Add($"{operation} failed: {message}", Severity.Error, o => o.VisibleStateDuration = 10000);
    }

    public void Info(string message, Severity severity = Severity.Info) => _snackbar.Add(message, severity);

    public void Notify() => Changed?.Invoke();

    private async void OnRepositoryChanged(object? sender, RepositoryChangedEventArgs e)
    {
        var session = _sessions.FirstOrDefault(s => ReferenceEquals(s.Repo, sender));
        if (session is null) return;
        if (session != _active)
        {
            session.StaleKinds |= e.Kind; // caught up when the tab is activated
            return;
        }

        if (IsBusy) return; // the operation refreshes when it completes
        try
        {
            await RefreshSessionAsync(session, e.Kind);
        }
        catch (Exception ex)
        {
            LogRefreshFailed(_log, ex);
        }
    }

    private void StartAutoFetch(RepoSession session)
    {
        session.AutoFetchCts?.Cancel();
        var cts = session.AutoFetchCts = new CancellationTokenSource();
        var settings = _settings.Current;
        _ = Task.Run(async () =>
        {
            try
            {
                if (settings.FetchOnOpen) await QuietFetchAsync(session, cts.Token);
                if (settings.AutoFetchMinutes <= 0) return;
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.AutoFetchMinutes));
                while (await timer.WaitForNextTickAsync(cts.Token)) await QuietFetchAsync(session, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private async Task QuietFetchAsync(RepoSession session, CancellationToken ct)
    {
        if (IsBusy || !_sessions.Contains(session)) return;
        try
        {
            if ((await session.Repo.QueryAsync(["remote"], ct)).StandardOutput.Trim().Length == 0) return;
            await session.Repo.Remotes.FetchAsync(new FetchRequest(), null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBackgroundFetchFailed(_log, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository refresh failed")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Operation} failed")]
    private static partial void LogOperationFailed(ILogger logger, Exception ex, string operation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background fetch failed")]
    private static partial void LogBackgroundFetchFailed(ILogger logger, Exception ex);

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.ToList())
        {
            s.Repo.Changed -= OnRepositoryChanged;
            await s.DisposeAsync();
        }

        _sessions.Clear();
        _active = null;
    }
}
