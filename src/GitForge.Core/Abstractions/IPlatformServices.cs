using GitForge.Core.Models;

namespace GitForge.Core.Abstractions;

/// <summary>OS keyring abstraction (Windows Credential Manager, libsecret, macOS Keychain). Secrets never touch disk in plaintext.</summary>
public interface ISecretStore
{
    string BackendName { get; }
    bool IsSupported { get; }
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>Per-user, non-secret settings persisted as JSON.</summary>
public sealed record AppSettings
{
    /// <summary>GitForge green (the logo's lime). Light mode shows it as a darker green for contrast on white.</summary>
    public const string DefaultAccentColor = "#86D24A";

    /// <summary>The blue default before the green theme; stored settings with it are moved to <see cref="DefaultAccentColor"/>.</summary>
    public const string LegacyDefaultAccentColor = "#3B82F6";

    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public string AccentColor { get; init; } = DefaultAccentColor;
    public bool VimNavigation { get; init; } = true;
    public string? GitExecutablePath { get; init; }
    public string? DefaultCloneDirectory { get; init; }
    public string? ExternalMergeTool { get; init; }
    public string? ExternalDiffTool { get; init; }
    public bool FetchOnOpen { get; init; } = true;

    /// <summary>Keep submodules in step with HEAD automatically (recursive init/sync/update; dirty submodules are skipped).</summary>
    public bool AutoUpdateSubmodules { get; init; } = true;
    public int AutoFetchMinutes { get; init; } = 10;
    public bool SideBySideDiff { get; init; }
    public bool SyntaxHighlighting { get; init; } = true;
    public bool ShowAuthorColumn { get; init; } = true;
    public bool ShowDateColumn { get; init; } = true;
    public bool ShowShaColumn { get; init; }
    public bool IgnoreWhitespace { get; init; }

    /// <summary>
    /// Keyboard shortcuts the user changed: command id → its complete list of key chords ("Ctrl+Shift+F", "g g"); an
    /// empty list unbinds the command. Commands not listed keep their defaults.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> KeyBindings { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Look for a new GitForge release once a day (installed copies only; machine policy can turn this off).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Custom update feed: an https URL or a folder / UNC share with GitForge releases. Null = GitHub releases.</summary>
    public string? UpdateFeed { get; init; }

    /// <summary>When the last automatic update check finished without finding an update (checks run at most daily).</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }

    public IReadOnlyList<string> RecentRepositories { get; init; } = [];

    /// <summary>Repository tabs open at the end of the last session (restored on launch).</summary>
    public IReadOnlyList<string> OpenRepositories { get; init; } = [];

    public string? ActiveRepository { get; init; }
    public IReadOnlyList<HostingAccount> Accounts { get; init; } = [];
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default);
}

public interface IHostingProvider
{
    HostingProviderKind Kind { get; }
    Task<HostingUser> ValidateAsync(HostingAccount account, string token, CancellationToken cancellationToken = default);
    IAsyncEnumerable<HostedRepository> ListRepositoriesAsync(HostingAccount account, string token, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HostedPullRequest>> ListPullRequestsAsync(HostingAccount account, string token, RemoteRepositoryRef repository, CancellationToken cancellationToken = default);

    /// <summary>Browser URL that opens the provider's "new pull/merge request" page prefilled.</summary>
    string GetCreatePullRequestUrl(HostingAccount? account, RemoteRepositoryRef repository, string sourceBranch, string targetBranch);
}

/// <summary>Account management: settings for metadata, keyring for tokens.</summary>
public interface IAccountService
{
    IReadOnlyList<HostingAccount> Accounts { get; }
    event EventHandler? AccountsChanged;
    Task<HostingUser> AddAsync(HostingAccount account, string token, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<string?> GetTokenAsync(HostingAccount account, CancellationToken cancellationToken = default);
    IHostingProvider GetProvider(HostingProviderKind kind);
    HostingAccount? FindAccountFor(RemoteRepositoryRef repository);
    HostingProviderKind? ResolveSelfHosted(string host);
}
