namespace GitForge.Core.Models;

public enum HostingProviderKind
{
    GitHub,
    GitLab,
    Bitbucket,
    AzureDevOps,
}

/// <summary>
/// A configured hosting account. Non-secret; persisted in settings. The token lives only in the OS keyring
/// under <see cref="SecretKey"/>.
/// </summary>
public sealed record HostingAccount
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required HostingProviderKind Provider { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Web host, e.g. https://github.com or https://gitlab.example.mil.</summary>
    public required Uri WebBaseUrl { get; init; }

    /// <summary>REST API root, e.g. https://api.github.com or https://ghe.example.com/api/v3.</summary>
    public required Uri ApiBaseUrl { get; init; }

    /// <summary>Login identity. For Bitbucket Cloud API tokens this must be the Atlassian account email.</summary>
    public string? Username { get; init; }

    /// <summary>Azure DevOps organization (cloud) or collection (server).</summary>
    public string? Organization { get; init; }

    public bool IsSelfHosted { get; init; }

    public string SecretKey => $"account:{Id:N}";
}

public sealed record HostingUser(string Login, string? DisplayName, string? WebUrl);

public sealed record HostedRepository(
    string FullName,
    string? Description,
    string CloneUrlHttps,
    string? CloneUrlSsh,
    string WebUrl,
    string? DefaultBranch,
    bool IsPrivate,
    DateTimeOffset? UpdatedAt);

public sealed record HostedPullRequest(
    string Id,
    string Title,
    string Author,
    string SourceBranch,
    string TargetBranch,
    string State,
    string WebUrl,
    DateTimeOffset? CreatedAt,
    bool IsDraft);

/// <summary>A remote URL resolved to a hosting provider and repository path.</summary>
public sealed record RemoteRepositoryRef(HostingProviderKind Provider, string Host, string Owner, string Repository, string? Project = null)
{
    /// <summary>owner/repo (GitHub, Bitbucket), group/subgroup/repo (GitLab), or project/repo (Azure DevOps).</summary>
    public string FullName => Project is null ? $"{Owner}/{Repository}" : $"{Project}/{Repository}";
}
