using GitForge.Core.Abstractions;
using GitForge.Core.Models;
using GitForge.Git.Engine;

namespace GitForge.Git;

/// <summary>Composition root for one open repository.</summary>
public sealed class GitRepository : IGitRepository
{
    private readonly RepoContext _ctx;
    private readonly RepositoryWatcher _watcher;
    private readonly LibGit2Worker _lib;

    internal GitRepository(string workingDirectory, string gitDirectory, IGitCli cli, GitEngineOptions options)
    {
        WorkingDirectory = workingDirectory;
        Name = Path.GetFileName(workingDirectory.TrimEnd('/', '\\'));
        _lib = new LibGit2Worker(workingDirectory);
        _watcher = new RepositoryWatcher(workingDirectory, gitDirectory, options.WatcherDebounce);
        _watcher.Changed += (_, e) => Changed?.Invoke(this, e);
        _ctx = new RepoContext(workingDirectory, gitDirectory, cli, _lib, _watcher, options);

        var reader = new RepositoryReader(_ctx);
        Read = reader;
        Index = new IndexOperations(_ctx);
        Branches = new BranchOperations(_ctx);
        Remotes = new RemoteOperations(_ctx);
        Stashes = new StashOperations(_ctx, reader);
        History = new HistoryOperations(_ctx);
        Submodules = new SubmoduleOperations(_ctx);
        Lfs = new LfsOperations(_ctx);
    }

    public string WorkingDirectory { get; }
    public string Name { get; }

    public event EventHandler<RepositoryChangedEventArgs>? Changed;

    public IRepositoryReader Read { get; }
    public IIndexOperations Index { get; }
    public IBranchOperations Branches { get; }
    public IRemoteOperations Remotes { get; }
    public IStashOperations Stashes { get; }
    public IHistoryOperations History { get; }
    public ISubmoduleOperations Submodules { get; }
    public ILfsOperations Lfs { get; }

    public Task<GitResult> RunRawAsync(IReadOnlyList<string> arguments, IProgress<string>? output = null, CancellationToken cancellationToken = default) =>
        _ctx.TryWriteAsync(arguments, cancellationToken, progress: output);

    public Task<GitResult> QueryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        _ctx.RunAsync(arguments, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        _lib.Dispose();
        return ValueTask.CompletedTask;
    }
}
