using GitForge.Core.Abstractions;
using GitForge.Core.Graph;
using GitForge.Core.Models;
using GitForge.Git;
using GitForge.Git.Submodules;
using GitForge.Git.Undo;
using Microsoft.Extensions.Logging;
using MudBlazor;

namespace GitForge.UI.Services;

/// <summary>What the center pane shows. The graph is home; diffs and the reflog open over it (Esc returns).</summary>
public enum CenterView
{
    Graph,
    Diff,
    Reflog,
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

/// <summary>
/// Per-window state for the active repository. Components subscribe to <see cref="Changed"/> (coarse refresh)
/// and <see cref="HistoryChanged"/> (graph rows appended). All git work runs off the UI thread.
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
    private CancellationTokenSource? _historyCts;
    private CancellationTokenSource? _autoFetchCts;
    private int _refreshing;
    private readonly Lock _refreshSync = new();
    private RepositoryChangeKind _pendingRefresh;

    public RepoWorkspace(IGitRepositoryFactory factory, ISettingsStore settings, IAccountService accounts, GitEngineOptions engine, ISnackbar snackbar, ILogger<RepoWorkspace> log)
    {
        _factory = factory;
        _settings = settings;
        _accounts = accounts;
        _engine = engine;
        _snackbar = snackbar;
        _log = log;
        _engine.SelfHostedResolver = _accounts.ResolveSelfHosted;
        Undo.Changed += (_, _) => Notify();
    }

    public IGitRepository? Repo { get; private set; }
    public RepositorySnapshot? Snapshot { get; private set; }

    /// <summary>Nested submodule status (<c>git submodule status --recursive</c>), refreshed with refs.</summary>
    public IReadOnlyList<SubmoduleStatusEntry> SubmoduleStatus { get; private set; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<RefLabel>> RefIndex { get; private set; } = new Dictionary<string, IReadOnlyList<RefLabel>>();
    public WorkingCopyStatus Status { get; private set; } = WorkingCopyStatus.Empty;
    public AppendOnlyList<GraphRow> Rows { get; private set; } = new();
    public bool HistoryLoading { get; private set; }
    public bool IsBusy => BusyText is not null;
    public string? BusyText { get; private set; }
    public string? ProgressText { get; private set; }
    public bool ConsoleOpen { get; set; }
    public List<string> ConsoleLines { get; } = [];
    public AppSettings Settings => _settings.Current;
    public UndoManager Undo { get; } = new();

    // Selection model: either the WIP node or one commit is selected; the commit panel follows it.
    public bool WipSelected { get; private set; }
    public string? SelectedCommitSha { get; private set; }
    public CenterView Center { get; private set; } = CenterView.Graph;

    /// <summary>Keyboard focus: graph (false) or commit panel (true). h/l switch; vim keys act on the focused side.</summary>
    public bool PanelFocused { get; set; }
    public DiffTarget? OpenDiff { get; private set; }

    // Hide / solo branches in the graph (canonical ref names).
    public HashSet<string> HiddenRefs { get; } = new(StringComparer.Ordinal);
    public string? SoloRef { get; private set; }
    public bool IsFilteringRefs => SoloRef is not null || HiddenRefs.Count > 0;

    /// <summary>Coarse change notification (snapshot, status, busy state, selection).</summary>
    public event Action? Changed;

    /// <summary>History rows were appended or replaced.</summary>
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

    // ---------- Selection & center pane ----------

    public void SelectWip()
    {
        WipSelected = true;
        SelectedCommitSha = null;
        Notify();
    }

    public void SelectCommit(string sha, bool reveal = false)
    {
        WipSelected = false;
        SelectedCommitSha = sha;
        Notify();
        if (reveal) RevealRequested?.Invoke(sha);
    }

    public void ShowGraph()
    {
        Center = CenterView.Graph;
        OpenDiff = null;
        Notify();
    }

    public void ShowReflog()
    {
        Center = CenterView.Reflog;
        Notify();
    }

    public void ShowDiff(DiffTarget target)
    {
        OpenDiff = target;
        Center = CenterView.Diff;
        Notify();
    }

    // ---------- Lifecycle ----------

    public async Task InitializeAsync()
    {
        var s = await _settings.LoadAsync();
        _engine.GitExecutablePath = s.GitExecutablePath;
    }

    public async Task OpenAsync(string path)
    {
        try
        {
            var root = _factory.Discover(path.Trim().Trim('"'));
            if (root is null)
            {
                _snackbar.Add($"Not a Git repository: {path}", Severity.Warning);
                return;
            }

            await CloseAsync();
            Repo = await _factory.OpenAsync(root);
            Repo.Changed += OnRepositoryChanged;
            await _settings.UpdateAsync(s => s with { RecentRepositories = [root, .. s.RecentRepositories.Where(r => !string.Equals(r, root, StringComparison.OrdinalIgnoreCase)).Take(19)] });

            await RefreshAsync(RepositoryChangeKind.All);
            if (HasWip) SelectWip();
            else if (Snapshot?.HeadSha is { } head) SelectCommit(head);
            await CheckSubmodulesOnOpenAsync();
            StartAutoFetch();
        }
        catch (Exception ex)
        {
            Error("Open repository", ex);
        }
    }

    public async Task CloseAsync()
    {
        _autoFetchCts?.Cancel();
        _historyCts?.Cancel();
        if (Repo is not null)
        {
            Repo.Changed -= OnRepositoryChanged;
            await Repo.DisposeAsync();
        }

        Repo = null;
        Snapshot = null;
        SubmoduleStatus = [];
        Status = WorkingCopyStatus.Empty;
        Rows = new AppendOnlyList<GraphRow>();
        SelectedCommitSha = null;
        WipSelected = false;
        Center = CenterView.Graph;
        OpenDiff = null;
        HiddenRefs.Clear();
        SoloRef = null;
        Undo.Clear();
        Notify();
        HistoryChanged?.Invoke();
    }

    /// <summary>Coalesces overlapping refresh requests: a running refresh picks up kinds queued while it works.</summary>
    public async Task RefreshAsync(RepositoryChangeKind kind = RepositoryChangeKind.All)
    {
        lock (_refreshSync) _pendingRefresh |= kind;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            while (true)
            {
                RepositoryChangeKind k;
                lock (_refreshSync)
                {
                    k = _pendingRefresh;
                    _pendingRefresh = RepositoryChangeKind.None;
                }

                var repo = Repo;
                if (k == RepositoryChangeKind.None || repo is null) break;

                var refs = (k & (RepositoryChangeKind.Refs | RepositoryChangeKind.Config)) != 0;
                if (refs)
                {
                    Snapshot = await repo.Read.GetSnapshotAsync();
                    RefIndex = Snapshot.BuildRefIndex();
                    SubmoduleStatus = Snapshot.Submodules.Count > 0 ? await repo.Submodules.GetRecursiveStatusAsync() : [];
                }

                Status = await repo.Read.GetStatusAsync();
                if (WipSelected && Status.IsClean && Snapshot?.HeadSha is { } head) SelectCommit(head);
                Notify();
                if (refs) _ = LoadHistoryAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRefreshFailed(_log, ex);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }

        bool again;
        lock (_refreshSync) again = _pendingRefresh != RepositoryChangeKind.None;
        if (again) await RefreshAsync(RepositoryChangeKind.None);
    }

    /// <summary>Streams history through the incremental graph layout, publishing rows in batches.</summary>
    public async Task LoadHistoryAsync()
    {
        var repo = Repo;
        if (repo is null) return;
        _historyCts?.Cancel();
        var cts = _historyCts = new CancellationTokenSource();
        var rows = new AppendOnlyList<GraphRow>();
        var builder = new CommitGraphBuilder(GitForgeTheme.LaneColors.Length);
        var query = BuildHistoryQuery();
        HistoryLoading = true;

        try
        {
            await Task.Run(async () =>
            {
                var nextPublish = FirstHistoryBatch;
                await foreach (var commit in repo.Read.GetHistoryAsync(query, cts.Token))
                {
                    rows.Add(builder.Add(commit));
                    if (rows.Count >= nextPublish)
                    {
                        Publish(rows, cts);
                        nextPublish = rows.Count + HistoryBatch;
                    }
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Error("Load history", ex);
        }

        if (cts.IsCancellationRequested) return;
        HistoryLoading = false;
        Publish(rows, cts);
        Notify();
    }

    private HistoryQuery BuildHistoryQuery()
    {
        if (SoloRef is { } solo) return new HistoryQuery { Refs = [solo] };
        if (HiddenRefs.Count == 0 || Snapshot is not { } s) return new HistoryQuery();

        // Tags are left out while hiding so they cannot pull hidden branches back into the graph.
        var refs = s.LocalBranches.Concat(s.RemoteBranches)
            .Select(b => b.CanonicalName)
            .Where(r => !HiddenRefs.Contains(r))
            .ToList();
        var currentHidden = CurrentBranchHead is { } cur && HiddenRefs.Contains(cur.CanonicalName);
        if (!currentHidden && s.HeadSha is not null) refs.Add("HEAD");
        return refs.Count == 0 ? new HistoryQuery { Refs = ["HEAD"] } : new HistoryQuery { Refs = refs };
    }

    public Task ToggleHiddenAsync(string canonicalRef)
    {
        if (!HiddenRefs.Remove(canonicalRef)) HiddenRefs.Add(canonicalRef);
        SoloRef = null;
        Notify();
        return LoadHistoryAsync();
    }

    public Task SoloAsync(string? canonicalRef)
    {
        SoloRef = SoloRef == canonicalRef ? null : canonicalRef;
        HiddenRefs.Clear();
        Notify();
        return LoadHistoryAsync();
    }

    public Task ShowAllRefsAsync()
    {
        HiddenRefs.Clear();
        SoloRef = null;
        Notify();
        return LoadHistoryAsync();
    }

    private void Publish(AppendOnlyList<GraphRow> rows, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested) return;
        Rows = rows;
        HistoryChanged?.Invoke();
    }

    // ---------- Operations ----------

    /// <summary>Runs a git operation with busy state, progress text, and user-facing error reporting.</summary>
    public async Task<bool> RunAsync(string label, Func<IGitRepository, IProgress<string>, Task> operation, string? success = null)
    {
        var repo = Repo;
        if (repo is null) return false;

        // Capture submodule pins so submodules can follow any HEAD move (checkout, pull, merge, rebase, reset, undo…).
        var beforeHead = Snapshot?.HeadSha;
        var beforePins = Settings.AutoUpdateSubmodules && Snapshot is { } snap ? SubmoduleAutoUpdater.Capture(snap) : null;

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
            await RefreshAsync(RepositoryChangeKind.All);
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
    public Task<bool> RunUndoableAsync(string label, Func<IGitRepository, Task<UndoEntry?>> operation, string? success = null) =>
        RunAsync(label, async (r, _) => Undo.Record(await operation(r)), success);

    public Task UndoAsync() => !Undo.CanUndo ? Task.CompletedTask
        : RunAsync($"Undoing {Undo.UndoDescription}", async (r, _) => Info($"Undid {await Undo.UndoAsync(r)}"));

    public Task RedoAsync() => !Undo.CanRedo ? Task.CompletedTask
        : RunAsync($"Redoing {Undo.RedoDescription}", async (r, _) => Info($"Redid {await Undo.RedoAsync(r)}"));

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
        var repo = Repo;
        if (repo is null || args.Count == 0) return null;
        ConsoleLines.Add("$ git " + string.Join(' ', args));
        BusyText = "git " + args[0];
        Notify();
        try
        {
            var result = await Task.Run(() => repo.RunRawAsync(args));
            foreach (var l in (result.StandardOutput + result.StandardError).Split('\n')) ConsoleLines.Add(l.TrimEnd('\r'));
            ConsoleLines.Add($"[exit {result.ExitCode}]");
            if (ConsoleLines.Count > 5000) ConsoleLines.RemoveRange(0, ConsoleLines.Count - 5000);
            return result;
        }
        catch (Exception ex)
        {
            ConsoleLines.Add("error: " + ex.Message);
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
        if (IsBusy) return; // the operation refreshes when it completes
        try
        {
            await RefreshAsync(e.Kind);
        }
        catch (Exception ex)
        {
            LogRefreshFailed(_log, ex);
        }
    }

    private void StartAutoFetch()
    {
        _autoFetchCts?.Cancel();
        var cts = _autoFetchCts = new CancellationTokenSource();
        var settings = _settings.Current;
        _ = Task.Run(async () =>
        {
            try
            {
                if (settings.FetchOnOpen) await QuietFetchAsync(cts.Token);
                if (settings.AutoFetchMinutes <= 0) return;
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.AutoFetchMinutes));
                while (await timer.WaitForNextTickAsync(cts.Token)) await QuietFetchAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private async Task QuietFetchAsync(CancellationToken ct)
    {
        var repo = Repo;
        if (repo is null || IsBusy || Snapshot?.Remotes.Count is null or 0) return;
        try
        {
            await repo.Remotes.FetchAsync(new FetchRequest(), null, ct);
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
        await CloseAsync();
        _historyCts?.Dispose();
        _autoFetchCts?.Dispose();
    }
}
