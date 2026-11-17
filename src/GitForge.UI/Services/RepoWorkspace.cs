using GitForge.Core.Abstractions;
using GitForge.Core.Graph;
using GitForge.Core.Models;
using GitForge.Git;
using Microsoft.Extensions.Logging;
using MudBlazor;

namespace GitForge.UI.Services;

public enum WorkspaceView
{
    FileStatus,
    History,
    Reflog,
}

/// <summary>
/// Per-window state container for the active repository. Components subscribe to <see cref="Changed"/>
/// (coarse UI refresh) and <see cref="HistoryChanged"/> (graph rows appended). All git work runs off the UI thread.
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
    }

    public IGitRepository? Repo { get; private set; }
    public RepositorySnapshot? Snapshot { get; private set; }
    public IReadOnlyDictionary<string, IReadOnlyList<RefLabel>> RefIndex { get; private set; } = new Dictionary<string, IReadOnlyList<RefLabel>>();
    public WorkingCopyStatus Status { get; private set; } = WorkingCopyStatus.Empty;
    public AppendOnlyList<GraphRow> Rows { get; private set; } = new();
    public bool HistoryLoading { get; private set; }
    public WorkspaceView View { get; private set; } = WorkspaceView.FileStatus;
    public string? SelectedCommitSha { get; set; }
    public bool IsBusy => BusyText is not null;
    public string? BusyText { get; private set; }
    public string? ProgressText { get; private set; }
    public bool ConsoleOpen { get; set; }
    public List<string> ConsoleLines { get; } = [];
    public AppSettings Settings => _settings.Current;

    /// <summary>Coarse change notification (snapshot, status, busy state, view).</summary>
    public event Action? Changed;

    /// <summary>History rows were appended or replaced.</summary>
    public event Action? HistoryChanged;

    public string? CurrentBranch => Snapshot?.HeadBranch;

    public BranchHead? CurrentBranchHead => Snapshot?.LocalBranches.FirstOrDefault(b => b.IsCurrent);

    public RemoteInfo? DefaultRemote =>
        Snapshot?.Remotes.FirstOrDefault(r => r.Name == CurrentBranchHead?.RemoteName)
        ?? Snapshot?.Remotes.FirstOrDefault(r => r.Name == "origin")
        ?? (Snapshot is { Remotes.Count: > 0 } s ? s.Remotes[0] : null);

    public void SetView(WorkspaceView view)
    {
        View = view;
        Notify();
    }

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
            View = Status.IsClean ? WorkspaceView.History : WorkspaceView.FileStatus;
            Notify();

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
        Status = WorkingCopyStatus.Empty;
        Rows = new AppendOnlyList<GraphRow>();
        SelectedCommitSha = null;
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
                }

                Status = await repo.Read.GetStatusAsync();
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

    /// <summary>Streams the full history through the incremental graph layout, publishing rows in batches.</summary>
    public async Task LoadHistoryAsync()
    {
        var repo = Repo;
        if (repo is null) return;
        _historyCts?.Cancel();
        var cts = _historyCts = new CancellationTokenSource();
        var rows = new AppendOnlyList<GraphRow>();
        var builder = new CommitGraphBuilder();
        HistoryLoading = true;

        try
        {
            await Task.Run(async () =>
            {
                var nextPublish = FirstHistoryBatch;
                await foreach (var commit in repo.Read.GetHistoryAsync(new HistoryQuery(), cts.Token))
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

    private void Publish(AppendOnlyList<GraphRow> rows, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested) return;
        Rows = rows;
        HistoryChanged?.Invoke();
    }

    /// <summary>Runs a git operation with busy state, progress text, and user-facing error reporting.</summary>
    public async Task<bool> RunAsync(string label, Func<IGitRepository, IProgress<string>, Task> operation, string? success = null)
    {
        var repo = Repo;
        if (repo is null) return false;

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

    public Task FetchAsync() => RunAsync("Fetching", (r, p) => r.Remotes.FetchAsync(new FetchRequest(), p), "Fetch complete");

    public Task PullAsync(bool rebase = false) => RunAsync("Pulling", (r, p) => r.Remotes.PullAsync(new PullChangesRequest(Rebase: rebase), p), "Pull complete");

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
