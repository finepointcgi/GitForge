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
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public string AccentColor { get; init; } = "#3B82F6";
    public bool VimNavigation { get; init; } = true;
    public string? GitExecutablePath { get; init; }
    public string? DefaultCloneDirectory { get; init; }
    public string? ExternalMergeTool { get; init; }
    public string? ExternalDiffTool { get; init; }
    public bool FetchOnOpen { get; init; } = true;
    public int AutoFetchMinutes { get; init; } = 10;
    public bool SideBySideDiff { get; init; }
    public bool ShowAuthorColumn { get; init; } = true;
    public bool ShowDateColumn { get; init; } = true;
    public bool ShowShaColumn { get; init; }
    public bool IgnoreWhitespace { get; init; }
    public IReadOnlyList<string> RecentRepositories { get; init; } = [];
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
