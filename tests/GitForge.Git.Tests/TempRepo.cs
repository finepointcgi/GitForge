using GitForge.Core.Abstractions;
using GitForge.Git.Cli;

namespace GitForge.Git.Tests;

/// <summary>Disposable scratch repository with deterministic, machine-independent config.</summary>
internal sealed class TempRepo : IAsyncDisposable
{
    public static readonly GitEngineOptions Options = new() { WatcherDebounce = TimeSpan.FromMilliseconds(50) };
    public static readonly GitCli Cli = new(Options);

    private TempRepo(string path) => Path = path;

    public string Path { get; }
    public IGitRepository? Repo { get; private set; }

    public static async Task<TempRepo> CreateAsync()
    {
        var dir = Directory.CreateTempSubdirectory("gitforge-test-").FullName;
        var t = new TempRepo(dir);
        await t.GitAsync("init", "--initial-branch=main", "-q");
        await t.GitAsync("config", "user.name", "Test User");
        await t.GitAsync("config", "user.email", "test@example.com");
        await t.GitAsync("config", "commit.gpgsign", "false");
        await t.GitAsync("config", "tag.gpgsign", "false");
        await t.GitAsync("config", "core.autocrlf", "false");
        await t.GitAsync("config", "core.hooksPath", System.IO.Path.Combine(dir, ".no-hooks"));
        return t;
    }

    public async Task<IGitRepository> OpenAsync()
    {
        Repo = await new GitRepositoryFactory(Cli, Options).OpenAsync(Path);
        return Repo;
    }

    public async Task<string> GitAsync(params string[] args)
    {
        // A stuck git (or git-for-windows shell script) fails the test fast and kills its process tree.
        var r = await Cli.RunAsync(new GitCommand(Path, args) { Timeout = TimeSpan.FromMinutes(2) });
        r.EnsureSuccess(string.Join(' ', args));
        return r.StandardOutput;
    }

    public void Write(string relative, string content) =>
        File.WriteAllText(System.IO.Path.Combine(Path, relative), content);

    public async Task CommitAllAsync(string message)
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", message);
    }

    public async ValueTask DisposeAsync()
    {
        if (Repo is not null) await Repo.DisposeAsync();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temp folder.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
