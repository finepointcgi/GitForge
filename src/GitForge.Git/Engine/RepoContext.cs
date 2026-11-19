using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Git.Engine;

/// <summary>Shared plumbing for the feature-area operation classes of one repository.</summary>
internal sealed class RepoContext(string workDir, string gitDir, IGitCli cli, LibGit2Worker lib, RepositoryWatcher watcher, GitEngineOptions options)
{
    public string WorkDir { get; } = workDir;
    public string GitDir { get; } = gitDir;
    public IGitCli Cli { get; } = cli;
    public LibGit2Worker Lib { get; } = lib;
    public RepositoryWatcher Watcher { get; } = watcher;
    public GitEngineOptions Options { get; } = options;

    public Task<GitResult> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        string? stdin = null,
        IProgress<string>? progress = null,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<int>? okCodes = null)
    {
        var cmd = new GitCommand(WorkDir, args)
        {
            StandardInput = stdin,
            Progress = progress,
            Environment = env,
            SuccessExitCodes = okCodes ?? [0],
        };
        return Cli.RunAsync(cmd, ct);
    }

    /// <summary>Runs a mutating command, refreshes caches, and throws <see cref="GitCommandException"/> on failure.</summary>
    public async Task<GitResult> WriteAsync(
        string operation,
        IReadOnlyList<string> args,
        CancellationToken ct,
        string? stdin = null,
        IProgress<string>? progress = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var result = await RunAsync(args, ct, stdin, progress, env).ConfigureAwait(false);
        AfterWrite();
        return result.EnsureSuccess(operation);
    }

    /// <summary>Same as <see cref="WriteAsync"/> but returns failures (conflicts, stops) to the caller.</summary>
    public async Task<GitResult> TryWriteAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        string? stdin = null,
        IProgress<string>? progress = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var result = await RunAsync(args, ct, stdin, progress, env).ConfigureAwait(false);
        AfterWrite();
        return result;
    }

    public void AfterWrite()
    {
        Lib.Invalidate();
        Watcher.Notify(RepositoryChangeKind.All);
    }

    /// <summary>NUL-separated pathspec payload for <c>--pathspec-from-file=- --pathspec-file-nul</c> (no command-line length limits).</summary>
    public static string PathspecPayload(IEnumerable<string> paths) => string.Join('\0', paths) + "\0";

    public static readonly string[] PathspecFromStdin = ["--pathspec-from-file=-", "--pathspec-file-nul"];

    /// <summary>Untranslated git messages, for commands whose output GitForge parses.</summary>
    public static readonly IReadOnlyDictionary<string, string> EnglishMessages = new Dictionary<string, string> { ["LC_ALL"] = "C" };

    /// <summary>
    /// A path inside the working tree for git commands: forward slashes, relative, no ".." segments, and not starting with
    /// "-" (so it can never be read as an option). Throws for anything else.
    /// </summary>
    public static string RelativePath(string path)
    {
        var p = path.Trim().Replace('\\', '/').Trim('/');
        if (p.Length == 0 || p.StartsWith('-') || Path.IsPathRooted(p) || p.Contains(':', StringComparison.Ordinal)
            || p.Split('/').Any(s => s is ".." or "." or ""))
        {
            throw new ArgumentException($"'{path}' must be a folder inside the repository, such as libs/name.", nameof(path));
        }

        return p;
    }
}
