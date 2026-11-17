using GitForge.Core.Models;

namespace GitForge.Git.Engine;

/// <summary>
/// Debounced file-system watcher that classifies changes (working tree, index, refs, config)
/// so the UI refreshes only what changed. Object-database and lock-file churn is ignored.
/// </summary>
internal sealed class RepositoryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string _gitDir;
    private readonly Timer _timer;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private RepositoryChangeKind _pending;

    public event EventHandler<RepositoryChangedEventArgs>? Changed;

    public RepositoryWatcher(string workingDirectory, string gitDirectory, TimeSpan debounce)
    {
        _gitDir = Path.GetFullPath(gitDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _debounce = debounce;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        AddWatcher(workingDirectory);
        if (!_gitDir.StartsWith(Path.GetFullPath(workingDirectory), StringComparison.OrdinalIgnoreCase))
        {
            AddWatcher(_gitDir); // linked worktrees and submodules keep the git dir elsewhere
        }
    }

    /// <summary>Lets the UI suppress self-inflicted refreshes or force one after a write.</summary>
    public void Notify(RepositoryChangeKind kind) => Queue(kind);

    private void AddWatcher(string path)
    {
        var w = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        w.Changed += OnEvent;
        w.Created += OnEvent;
        w.Deleted += OnEvent;
        w.Renamed += OnEvent;
        w.Error += (_, _) => Queue(RepositoryChangeKind.All); // buffer overflow: refresh everything
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Queue(Classify(e.FullPath));

    internal RepositoryChangeKind Classify(string fullPath)
    {
        var p = Path.GetFullPath(fullPath);
        if (!p.StartsWith(_gitDir, StringComparison.OrdinalIgnoreCase) &&
            !p.Equals(_gitDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryChangeKind.WorkingTree;
        }

        var rel = p.Length > _gitDir.Length ? p[_gitDir.Length..].Replace('\\', '/') : string.Empty;
        if (rel.EndsWith(".lock", StringComparison.Ordinal) || rel.StartsWith("objects/", StringComparison.Ordinal)) return RepositoryChangeKind.None;
        if (rel == "index") return RepositoryChangeKind.Index;
        if (rel == "config") return RepositoryChangeKind.Config;
        if (rel is "HEAD" or "packed-refs" or "FETCH_HEAD" or "ORIG_HEAD" or "MERGE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD"
            || rel.StartsWith("refs/", StringComparison.Ordinal)
            || rel.StartsWith("rebase-merge", StringComparison.Ordinal)
            || rel.StartsWith("rebase-apply", StringComparison.Ordinal))
        {
            return RepositoryChangeKind.Refs;
        }

        return RepositoryChangeKind.None;
    }

    private void Queue(RepositoryChangeKind kind)
    {
        if (kind == RepositoryChangeKind.None) return;
        lock (_gate)
        {
            _pending |= kind;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        RepositoryChangeKind kind;
        lock (_gate)
        {
            kind = _pending;
            _pending = RepositoryChangeKind.None;
        }

        if (kind != RepositoryChangeKind.None) Changed?.Invoke(this, new RepositoryChangedEventArgs(kind));
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}
