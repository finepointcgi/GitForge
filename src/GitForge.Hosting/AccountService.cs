using GitForge.Core.Abstractions;
using GitForge.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GitForge.Hosting;

/// <summary>Builds correctly-shaped account records for cloud and self-hosted instances.</summary>
public static class HostingDefaults
{
    /// <param name="selfHostedUrl">
    /// Base web URL for self-hosted instances (https:// or http://); null for the public cloud, which is always HTTPS.
    /// </param>
    /// <param name="organization">Azure DevOps organization (cloud) — required for Azure DevOps Services.</param>
    public static HostingAccount CreateAccount(HostingProviderKind kind, string displayName, string? username, string? selfHostedUrl = null, string? organization = null)
    {
        Uri? custom = null;
        if (!string.IsNullOrWhiteSpace(selfHostedUrl))
        {
            if (!Uri.TryCreate(selfHostedUrl.Trim().TrimEnd('/'), UriKind.Absolute, out custom) || !IsWebScheme(custom))
            {
                throw new ArgumentException("Self-hosted URL must be an absolute http:// or https:// URL.", nameof(selfHostedUrl));
            }
        }

        var (web, api) = kind switch
        {
            HostingProviderKind.GitHub when custom is null => (new Uri("https://github.com"), new Uri("https://api.github.com")),
            HostingProviderKind.GitHub => (custom, new Uri($"{Root(custom)}/api/v3")),
            HostingProviderKind.GitLab => (custom ?? new Uri("https://gitlab.com"), new Uri($"{Root(custom ?? new Uri("https://gitlab.com"))}/api/v4")),
            HostingProviderKind.Bitbucket when custom is null => (new Uri("https://bitbucket.org"), new Uri("https://api.bitbucket.org/2.0")),
            HostingProviderKind.Bitbucket => (custom, new Uri($"{Root(custom)}/rest/api/1.0")),
            HostingProviderKind.AzureDevOps when custom is null => AzureCloud(organization),
            HostingProviderKind.AzureDevOps => (custom, custom),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return new HostingAccount
        {
            Provider = kind,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"{kind} ({web.Host})" : displayName.Trim(),
            WebBaseUrl = web,
            ApiBaseUrl = api,
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            Organization = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim(),
            IsSelfHosted = custom is not null,
        };
    }

    // Uri.ToString() appends '/' to an empty path; strip it before composing API roots.
    private static string Root(Uri u) => u.ToString().TrimEnd('/');

    internal static bool IsWebScheme(Uri u) => u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp;

    /// <summary>
    /// Request policy: HTTPS always; plain HTTP only when the account itself was configured with an http:// API root,
    /// and only to that same host and port (no scheme downgrade or cross-host hop from pagination links).
    /// </summary>
    internal static bool IsRequestAllowed(Uri request, HostingAccount account)
    {
        if (request.Scheme == Uri.UriSchemeHttps) return true;
        return request.Scheme == Uri.UriSchemeHttp
               && account.IsInsecureHttp
               && string.Equals(request.Host, account.ApiBaseUrl.Host, StringComparison.OrdinalIgnoreCase)
               && request.Port == account.ApiBaseUrl.Port;
    }

    private static (Uri, Uri) AzureCloud(string? organization)
    {
        if (string.IsNullOrWhiteSpace(organization)) throw new ArgumentException("Azure DevOps organization is required.", nameof(organization));
        var u = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization.Trim())}");
        return (u, u);
    }
}

/// <summary>Multi-account manager: metadata in settings, tokens in the OS keyring, validated before saving.</summary>
public sealed class AccountService : IAccountService
{
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly Dictionary<HostingProviderKind, IHostingProvider> _providers;

    public AccountService(ISettingsStore settings, ISecretStore secrets, IEnumerable<IHostingProvider> providers)
    {
        _settings = settings;
        _secrets = secrets;
        _providers = providers.ToDictionary(p => p.Kind);
        _settings.Changed += (_, _) => AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<HostingAccount> Accounts => _settings.Current.Accounts;
    public event EventHandler? AccountsChanged;

    public IHostingProvider GetProvider(HostingProviderKind kind) =>
        _providers.TryGetValue(kind, out var p) ? p : throw new NotSupportedException($"No provider registered for {kind}.");

    public async Task<HostingUser> AddAsync(HostingAccount account, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!HostingDefaults.IsWebScheme(account.ApiBaseUrl)) throw new ArgumentException("Only http:// and https:// endpoints are supported.", nameof(account));
        if (!_secrets.IsSupported) throw new PlatformNotSupportedException($"Secure token storage is unavailable ({_secrets.BackendName}).");

        var user = await GetProvider(account.Provider).ValidateAsync(account, token.Trim(), cancellationToken).ConfigureAwait(false);
        await _secrets.SetAsync(account.SecretKey, token.Trim(), cancellationToken).ConfigureAwait(false);
        await _settings.UpdateAsync(s => s with { Accounts = [.. s.Accounts.Where(a => a.Id != account.Id), account] }, cancellationToken).ConfigureAwait(false);
        return user;
    }

    public async Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;
        await _secrets.DeleteAsync(account.SecretKey, cancellationToken).ConfigureAwait(false);
        await _settings.UpdateAsync(s => s with { Accounts = [.. s.Accounts.Where(a => a.Id != accountId)] }, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> GetTokenAsync(HostingAccount account, CancellationToken cancellationToken = default) =>
        _secrets.GetAsync(account.SecretKey, cancellationToken);

    public HostingAccount? FindAccountFor(RemoteRepositoryRef repository)
    {
        var candidates = Accounts.Where(a => a.Provider == repository.Provider).ToList();
        if (repository.Provider == HostingProviderKind.AzureDevOps)
        {
            return candidates.FirstOrDefault(a => string.Equals(a.Organization, repository.Owner, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(a => a.IsSelfHosted && string.Equals(a.WebBaseUrl.Host, repository.Host, StringComparison.OrdinalIgnoreCase));
        }

        return candidates.FirstOrDefault(a => HostMatches(a, repository.Host));
    }

    public HostingProviderKind? ResolveSelfHosted(string host) =>
        Accounts.FirstOrDefault(a => a.IsSelfHosted && string.Equals(a.WebBaseUrl.Host, host, StringComparison.OrdinalIgnoreCase))?.Provider;

    private static bool HostMatches(HostingAccount account, string host)
    {
        var web = account.WebBaseUrl.Host;
        if (string.Equals(web, host, StringComparison.OrdinalIgnoreCase)) return true;
        // SSH remotes for GitHub/GitLab Enterprise sometimes use an "ssh." prefix.
        return host.StartsWith("ssh.", StringComparison.OrdinalIgnoreCase) && string.Equals(web, host[4..], StringComparison.OrdinalIgnoreCase);
    }
}

public static class HostingServiceCollectionExtensions
{
    public static IServiceCollection AddGitForgeHosting(this IServiceCollection services)
    {
        services.AddHttpClient(HostingProviderBase.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IHostingProvider, GitHubProvider>();
        services.AddSingleton<IHostingProvider, GitLabProvider>();
        services.AddSingleton<IHostingProvider, BitbucketProvider>();
        services.AddSingleton<IHostingProvider, AzureDevOpsProvider>();
        services.AddSingleton<IAccountService, AccountService>();
        return services;
    }
}
