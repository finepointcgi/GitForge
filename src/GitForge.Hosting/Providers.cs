using System.Runtime.CompilerServices;
using System.Text.Json;
using GitForge.Core.Models;

namespace GitForge.Hosting;

/// <summary>GitHub.com and GitHub Enterprise Server (REST v3, API version 2022-11-28).</summary>
public sealed class GitHubProvider(IHttpClientFactory factory) : HostingProviderBase(factory)
{
    public override HostingProviderKind Kind => HostingProviderKind.GitHub;

    protected override void Authorize(HttpRequestMessage request, HostingAccount account, string token)
    {
        Bearer(request, token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public override async Task<HostingUser> ValidateAsync(HostingAccount account, string token, CancellationToken cancellationToken = default)
    {
        var (json, response) = await GetAsync(Combine(account.ApiBaseUrl, "user"), account, token, cancellationToken).ConfigureAwait(false);
        using (json)
        using (response)
        {
            var r = json.RootElement;
            return new HostingUser(Str(r, "login") ?? "unknown", Str(r, "name"), Str(r, "html_url"));
        }
    }

    public override async IAsyncEnumerable<HostedRepository> ListRepositoriesAsync(HostingAccount account, string token, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Uri? next = Combine(account.ApiBaseUrl, "user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member");
        while (next is not null)
        {
            var (json, response) = await GetAsync(next, account, token, cancellationToken).ConfigureAwait(false);
            using (json)
            using (response)
            {
                foreach (var r in json.RootElement.EnumerateArray())
                {
                    yield return new HostedRepository(
                        Str(r, "full_name") ?? string.Empty, Str(r, "description"), Str(r, "clone_url") ?? string.Empty, Str(r, "ssh_url"),
                        Str(r, "html_url") ?? string.Empty, Str(r, "default_branch"), Bool(r, "private"), Date(r, "updated_at"));
                }

                next = NextLink(response);
            }
        }
    }

    public override async Task<IReadOnlyList<HostedPullRequest>> ListPullRequestsAsync(HostingAccount account, string token, RemoteRepositoryRef repository, CancellationToken cancellationToken = default)
    {
        var uri = Combine(account.ApiBaseUrl, $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Repository)}/pulls?state=open&per_page=50");
        var (json, response) = await GetAsync(uri, account, token, cancellationToken).ConfigureAwait(false);
        using (json)
        using (response)
        {
            return json.RootElement.EnumerateArray().Select(p => new HostedPullRequest(
                Str(p, "number") ?? string.Empty, Str(p, "title") ?? string.Empty, Str(p, "user", "login") ?? string.Empty,
                Str(p, "head", "ref") ?? string.Empty, Str(p, "base", "ref") ?? string.Empty, Str(p, "state") ?? "open",
                Str(p, "html_url") ?? string.Empty, Date(p, "created_at"), Bool(p, "draft"))).ToList();
        }
    }

    public override string GetCreatePullRequestUrl(HostingAccount? account, RemoteRepositoryRef repository, string sourceBranch, string targetBranch)
    {
        var web = account?.WebBaseUrl.ToString().TrimEnd('/') ?? $"https://{repository.Host}";
        return $"{web}/{repository.Owner}/{repository.Repository}/compare/{EscapeBranch(targetBranch)}...{EscapeBranch(sourceBranch)}?expand=1";
    }
}

/// <summary>GitLab.com and GitLab self-managed (REST v4).</summary>
public sealed class GitLabProvider(IHttpClientFactory factory) : HostingProviderBase(factory)
{
    public override HostingProviderKind Kind => HostingProviderKind.GitLab;

    protected override void Authorize(HttpRequestMessage request, HostingAccount account, string token) => Bearer(request, token);

    public override async Task<HostingUser> ValidateAsync(HostingAccount account, string token, CancellationToken cancellationToken = default)
    {
        var (json, response) = await GetAsync(Combine(account.ApiBaseUrl, "user"), account, token, cancellationToken).ConfigureAwait(false);
        using (json)
        using (response)
        {
            var r = json.RootElement;
            return new HostingUser(Str(r, "username") ?? "unknown", Str(r, "name"), Str(r, "web_url"));
        }
    }

    public override async IAsyncEnumerable<HostedRepository> ListRepositoriesAsync(HostingAccount account, string token, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Uri? next = Combine(account.ApiBaseUrl, "projects?membership=true&simple=true&per_page=100&order_by=last_activity_at");
        while (next is not null)
        {
            var (json, response) = await GetAsync(next, account, token, cancellationToken).ConfigureAwait(false);
            using (json)
            using (response)
            {
                foreach (var r in json.RootElement.EnumerateArray())
                {
                    yield return new HostedRepository(
                        Str(r, "path_with_namespace") ?? string.Empty, Str(r, "description"), Str(r, "http_url_to_repo") ?? string.Empty, Str(r, "ssh_url_to_repo"),
                        Str(r, "web_url") ?? string.Empty, Str(r, "default_branch"), Str(r, "visibility") != "public", Date(r, "last_activity_at"));
                }

                next = NextLink(response);
            }
        }
    }

    public override async Task<IReadOnlyList<HostedPullRequest>> ListPullRequestsAsync(HostingAccount account, string token, RemoteRepositoryRef repository, CancellationToken cancellationToken = default)
    {
        var id = Uri.EscapeDataString($"{repository.Owner}/{repository.Repository}");
        var (json, response) = await GetAsync(Combine(account.ApiBaseUrl, $"projects/{id}/merge_requests?state=opened&per_page=50"), account, token, cancellationToken).ConfigureAwait(false);
        using (json)
        using (response)
        {
            return json.RootElement.EnumerateArray().Select(p => new HostedPullRequest(
                Str(p, "iid") ?? string.Empty, Str(p, "title") ?? string.Empty, Str(p, "author", "username") ?? string.Empty,
                Str(p, "source_branch") ?? string.Empty, Str(p, "target_branch") ?? string.Empty, Str(p, "state") ?? "opened",
                Str(p, "web_url") ?? string.Empty, Date(p, "created_at"), Bool(p, "draft"))).ToList();
        }
    }

    public override string GetCreatePullRequestUrl(HostingAccount? account, RemoteRepositoryRef repository, string sourceBranch, string targetBranch)
    {
        var web = account?.WebBaseUrl.ToString().TrimEnd('/') ?? $"https://{repository.Host}";
        return $"{web}/{repository.Owner}/{repository.Repository}/-/merge_requests/new" +
               $"?merge_request%5Bsource_branch%5D={Uri.EscapeDataString(sourceBranch)}&merge_request%5Btarget_branch%5D={Uri.EscapeDataString(targetBranch)}";
    }
}

/// <summary>
/// Bitbucket Cloud (REST 2.0). User API tokens authenticate with HTTP Basic (Atlassian account email + token);
/// workspace/project/repository access tokens use Bearer (leave the username empty). Repositories are listed per
/// workspace because Atlassian removed the cross-workspace repository endpoints in 2026.
/// </summary>
public sealed class BitbucketProvider(IHttpClientFactory factory) : HostingProviderBase(factory)
{
    public override HostingProviderKind Kind => HostingProviderKind.Bitbucket;

    protected override void Authorize(HttpRequestMessage request, HostingAccount account, string token)
    {
        if (account.IsSelfHosted) throw new NotSupportedException("Bitbucket Data Center uses a different API (/rest/api/1.0) and is planned for a later phase.");
        if (string.IsNullOrWhiteSpace(account.Username)) Bearer(request, token);
        else Basic(request, account.Username, token);
    }

    public override async Task<HostingUser> ValidateAsync(HostingAccount account, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account.Username))
        {
            // Access tokens are not tied to a user; prove access by listing one workspace page instead.
            var (ws, wsResponse) = await GetAsync(Combine(account.ApiBaseUrl, "user/workspaces?pagelen=1"), account, token, cancellationToken).ConfigureAwait(false);
            ws.Dispose();
            wsResponse.Dispose();
            return new HostingUser("access-token", account.DisplayName, null);
        }

        var (json, response) = await GetAsync(Combine(account.ApiBaseUrl, "user"), account, token, cancellationToken).ConfigureAwait(false);
        using (json)
        using (response)
        {
            var r = json.RootElement;
            return new HostingUser(Str(r, "nickname") ?? account.Username, Str(r, "display_name"), Str(r, "links", "html", "href"));
        }
    }

    public override async IAsyncEnumerable<HostedRepository> ListRepositoriesAsync(HostingAccount account, string token, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var workspaces = new List<string>();
        await foreach (var w in PagesAsync(Combine(account.ApiBaseUrl, "user/workspaces?pagelen=100"), account, token, cancellationToken).ConfigureAwait(false))
        {
            var slug = Str(w, "workspace", "slug") ?? Str(w, "slug");
            if (slug is not null) workspaces.Add(slug);
        }

        foreach (var slug in workspaces)
        {
            var uri = Combine(account.ApiBaseUrl, $"repositories/{Uri.EscapeDataString(slug)}?role=member&pagelen=100&sort=-updated_on");
            await foreach (var r in PagesAsync(uri, account, token, cancellationToken).ConfigureAwait(false))
            {
                string? https = null, ssh = null;
                if (r.TryGetProperty("links", out var links) && links.TryGetProperty("clone", out var clone) && clone.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in clone.EnumerateArray())
                    {
                        if (Str(c, "name") == "https") https = Str(c, "href");
                        else if (Str(c, "name") == "ssh") ssh = Str(c, "href");
                    }
                }

                yield return new HostedRepository(
                    Str(r, "full_name") ?? string.Empty, Str(r, "description"), https ?? string.Empty, ssh,
                    Str(r, "links", "html", "href") ?? string.Empty, Str(r, "mainbranch", "name"), Bool(r, "is_private"), Date(r, "updated_on"));
            }
        }
    }

    public override async Task<IReadOnlyList<HostedPullRequest>> ListPullRequestsAsync(HostingAccount account, string token, RemoteRepositoryRef repository, CancellationToken cancellationToken = default)
    {
        var uri = Combine(account.ApiBaseUrl, $"repositories/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Repository)}/pullrequests?state=OPEN&pagelen=50");
        var list = new List<HostedPullRequest>();
        await foreach (var p in PagesAsync(uri, account, token, cancellationToken, maxPages: 1).ConfigureAwait(false))
        {
            list.Add(new HostedPullRequest(
                Str(p, "id") ?? string.Empty, Str(p, "title") ?? string.Empty, Str(p, "author", "display_name") ?? string.Empty,
                Str(p, "source", "branch", "name") ?? string.Empty, Str(p, "destination", "branch", "name") ?? string.Empty,
                Str(p, "state") ?? "OPEN", Str(p, "links", "html", "href") ?? string.Empty, Date(p, "created_on"), Bool(p, "draft")));
        }

        return list;
    }

    public override string GetCreatePullRequestUrl(HostingAccount? account, RemoteRepositoryRef repository, string sourceBranch, string targetBranch) =>
        $"https://bitbucket.org/{repository.Owner}/{repository.Repository}/pull-requests/new?source={Uri.EscapeDataString(sourceBranch)}&dest={Uri.EscapeDataString(targetBranch)}&t=1";

    /// <summary>Bitbucket paginates with a "next" URL in the body.</summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(Uri first, HostingAccount account, string token, [EnumeratorCancellation] CancellationToken ct, int maxPages = 50)
    {
        Uri? next = first;
        for (var page = 0; next is not null && page < maxPages; page++)
        {
            var (json, response) = await GetAsync(next, account, token, ct).ConfigureAwait(false);
            using (json)
            using (response)
            {
                if (json.RootElement.TryGetProperty("values", out var values))
                {
                    foreach (var v in values.EnumerateArray()) yield return v.Clone();
                }

                var n = Str(json.RootElement, "next");
                next = n is not null && Uri.TryCreate(n, UriKind.Absolute, out var u) && u.Host == first.Host ? u : null;
            }
        }
    }
}

/// <summary>Azure DevOps Services and Azure DevOps Server (REST api-version 7.1). PATs use HTTP Basic with an empty user name.</summary>
public sealed class AzureDevOpsProvider(IHttpClientFactory factory) : HostingProviderBase(factory)
{
    private const string ApiVersion = "api-version=7.1";

    public override HostingProviderKind Kind => HostingProviderKind.AzureDevOps;

    protected override void Authorize(HttpRequestMessage request, HostingAccount account, string token)
    {
        Basic(request, string.Empty, token);
        request.Headers.Accept.ParseAdd("application/json");
    }

    public override async Task<HostingUser> ValidateAsync(HostingAccount account, string token, CancellationToken cancellationToken = default)
    {
        var (json, response) = await GetAsync(Combine(account.ApiBaseUrl, $"_apis/projects?$top=1&{ApiVersion}"), account, token, cancellationToken).ConfigureAwait(false);
        json.Dispose();
        response.Dispose();
        return new HostingUser(account.Username ?? "PAT", account.Organization, account.WebBaseUrl.ToString());
    }

    public override async IAsyncEnumerable<HostedRepository> ListRepositoriesAsync(HostingAccount account, string token, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (json, response) = await GetAsync(Combine(account.ApiBaseUrl, $"_apis/git/repositories?{ApiVersion}"), account, token, cancellationToken).ConfigureAwait(false);
        using (json)
        using (response)
        {
            foreach (var r in json.RootElement.GetProperty("value").EnumerateArray())
            {
                if (Bool(r, "isDisabled")) continue;
                var defaultBranch = Str(r, "defaultBranch");
                yield return new HostedRepository(
                    $"{Str(r, "project", "name")}/{Str(r, "name")}", null, Str(r, "remoteUrl") ?? string.Empty, Str(r, "sshUrl"),
                    Str(r, "webUrl") ?? string.Empty, defaultBranch?.Replace("refs/heads/", string.Empty, StringComparison.Ordinal),
                    Str(r, "project", "visibility") != "public", null);
            }
        }
    }

    public override async Task<IReadOnlyList<HostedPullRequest>> ListPullRequestsAsync(HostingAccount account, string token, RemoteRepositoryRef repository, CancellationToken cancellationToken = default)
    {
        var project = Uri.EscapeDataString(repository.Project ?? repository.Repository);
        var repo = Uri.EscapeDataString(repository.Repository);
        var uri = Combine(account.ApiBaseUrl, $"{project}/_apis/git/repositories/{repo}/pullrequests?searchCriteria.status=active&$top=50&{ApiVersion}");
        var (json, response) = await GetAsync(uri, account, token, cancellationToken).ConfigureAwait(false);
        using (json)
        using (response)
        {
            static string Short(string? r) => r?.Replace("refs/heads/", string.Empty, StringComparison.Ordinal) ?? string.Empty;
            return json.RootElement.GetProperty("value").EnumerateArray().Select(p =>
            {
                var id = Str(p, "pullRequestId") ?? string.Empty;
                return new HostedPullRequest(
                    id, Str(p, "title") ?? string.Empty, Str(p, "createdBy", "displayName") ?? string.Empty,
                    Short(Str(p, "sourceRefName")), Short(Str(p, "targetRefName")), Str(p, "status") ?? "active",
                    $"{Str(p, "repository", "webUrl")}/pullrequest/{id}", Date(p, "creationDate"), Bool(p, "isDraft"));
            }).ToList();
        }
    }

    public override string GetCreatePullRequestUrl(HostingAccount? account, RemoteRepositoryRef repository, string sourceBranch, string targetBranch)
    {
        var web = account?.WebBaseUrl.ToString().TrimEnd('/') ?? $"https://dev.azure.com/{repository.Owner}";
        return $"{web}/{Uri.EscapeDataString(repository.Project ?? repository.Repository)}/_git/{Uri.EscapeDataString(repository.Repository)}/pullrequestcreate" +
               $"?sourceRef={Uri.EscapeDataString(sourceBranch)}&targetRef={Uri.EscapeDataString(targetBranch)}";
    }
}
