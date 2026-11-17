namespace GitForge.Core.Abstractions;

/// <summary>A git CLI invocation. Arguments are passed as an argv list (never through a shell) to prevent injection.</summary>
public sealed record GitCommand(string WorkingDirectory, IReadOnlyList<string> Arguments)
{
    public string? StandardInput { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Receives progress lines from stderr (e.g. "Receiving objects: 42%").</summary>
    public IProgress<string>? Progress { get; init; }

    /// <summary>Exit codes treated as success (e.g. <c>git diff --no-index</c> returns 1 when files differ).</summary>
    public IReadOnlyList<int> SuccessExitCodes { get; init; } = [0];

    public TimeSpan? Timeout { get; init; }

    public override string ToString() => "git " + string.Join(' ', Arguments);
}

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, bool Succeeded)
{
    public GitResult EnsureSuccess(string? operation = null)
    {
        if (!Succeeded) throw new GitCommandException(operation ?? "git", ExitCode, StandardError.Trim().Length > 0 ? StandardError.Trim() : StandardOutput.Trim());
        return this;
    }
}

public sealed class GitCommandException(string operation, int exitCode, string message)
    : Exception($"{operation} failed (exit {exitCode}): {message}")
{
    public string Operation { get; } = operation;
    public int ExitCode { get; } = exitCode;
    public string GitMessage { get; } = message;
}

public interface IGitCli
{
    /// <summary>Resolved path of the git executable.</summary>
    string GitPath { get; }

    Task<GitResult> RunAsync(GitCommand command, CancellationToken cancellationToken = default);

    /// <summary>Streams stdout records split on <paramref name="separator"/> without buffering the full output.</summary>
    IAsyncEnumerable<string> StreamAsync(GitCommand command, char separator = '\n', CancellationToken cancellationToken = default);

    Task<Version?> GetVersionAsync(CancellationToken cancellationToken = default);
}
