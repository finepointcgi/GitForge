using System.Collections.Concurrent;
using LibGit2Sharp;

namespace GitForge.Git.Engine;

/// <summary>
/// Serializes all libgit2 access for one repository onto a dedicated background thread.
/// LibGit2Sharp <see cref="Repository"/> instances are not thread-safe; this actor keeps the UI thread free
/// and guarantees one-at-a-time access without lock contention in callers.
/// </summary>
internal sealed class LibGit2Worker : IDisposable
{
    private readonly string _path;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private Repository? _repository;
    private volatile bool _invalidated;

    public LibGit2Worker(string path)
    {
        _path = path;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"libgit2:{Path.GetFileName(path.TrimEnd('/', '\\'))}" };
        _thread.Start();
    }

    /// <summary>Drops the cached handle so the next call re-opens it (used after CLI writes).</summary>
    public void Invalidate() => _invalidated = true;

    public Task<T> RunAsync<T>(Func<Repository, T> work, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);

        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(work(GetRepository()));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, CancellationToken.None);
        return tcs.Task;
    }

    private Repository GetRepository()
    {
        if (_invalidated && _repository is not null)
        {
            _repository.Dispose();
            _repository = null;
        }

        _invalidated = false;
        return _repository ??= new Repository(_path);
    }

    private void Loop()
    {
        foreach (var item in _queue.GetConsumingEnumerable()) item();
        _repository?.Dispose();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
