using GitForge.Core.Models;

namespace GitForge.Git;

/// <summary>Runtime options for the git engine. Hosts update these after loading user settings.</summary>
public sealed class GitEngineOptions
{
    /// <summary>Explicit git executable; when null, PATH and standard install locations are searched.</summary>
    public string? GitExecutablePath { get; set; }

    /// <summary>Maps a host name to a provider for self-hosted instances (GitHub Enterprise, GitLab self-managed, etc.).</summary>
    public Func<string, HostingProviderKind?>? SelfHostedResolver { get; set; }

    /// <summary>File-system change debounce window.</summary>
    public TimeSpan WatcherDebounce { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Minimum supported git version (uses --pathspec-from-file, --diff-merges, --end-of-options).</summary>
    public static readonly Version MinimumGitVersion = new(2, 39, 0);
}
