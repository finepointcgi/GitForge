using GitForge.Core.Models;

namespace GitForge.Core.Hosting;

/// <summary>Resolves Git remote URLs (HTTPS, SSH, scp-style) to a hosting provider and repository path.</summary>
public static class RemoteUrlParser
{
    /// <param name="url">Remote URL as stored in git config.</param>
    /// <param name="selfHostedResolver">Maps a host name to a provider for self-hosted instances (from configured accounts).</param>
    public static RemoteRepositoryRef? Parse(string url, Func<string, HostingProviderKind?>? selfHostedResolver = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!TrySplit(url.Trim(), out var host, out var path)) return null;

        host = host.ToLowerInvariant();
        path = Uri.UnescapeDataString(path.Trim('/'));
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Azure DevOps (cloud) has distinctive host names and URL shapes.
        if (host is "dev.azure.com" or "ssh.dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            return ParseAzure(host, segments);
        }

        HostingProviderKind? kind = host switch
        {
            "github.com" => HostingProviderKind.GitHub,
            "gitlab.com" => HostingProviderKind.GitLab,
            "bitbucket.org" => HostingProviderKind.Bitbucket,
            _ => selfHostedResolver?.Invoke(host),
        };

        if (kind is null || segments.Length < 2) return null;
        if (kind == HostingProviderKind.AzureDevOps) return ParseAzure(host, segments);

        // GitLab supports nested groups: everything but the last segment is the namespace.
        var owner = kind == HostingProviderKind.GitLab
            ? string.Join('/', segments[..^1])
            : segments[^2];
        return new RemoteRepositoryRef(kind.Value, host, owner, segments[^1]);
    }

    private static RemoteRepositoryRef? ParseAzure(string host, string[] s)
    {
        // ssh.dev.azure.com:v3/{org}/{project}/{repo}
        if (s.Length >= 4 && s[0] == "v3") return new RemoteRepositoryRef(HostingProviderKind.AzureDevOps, host, s[1], s[3], s[2]);

        var git = Array.IndexOf(s, "_git");
        if (git < 1 || git + 1 >= s.Length) return null;
        var repo = s[git + 1];

        // dev.azure.com/{org}/{project}/_git/{repo}  |  {org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}
        string org;
        string project;
        if (host == "dev.azure.com")
        {
            org = s[0];
            project = git >= 2 ? s[git - 1] : repo;
        }
        else if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            org = host[..host.IndexOf('.', StringComparison.Ordinal)];
            project = s[git - 1];
        }
        else
        {
            // Azure DevOps Server: {host}/[tfs/]{collection}/{project}/_git/{repo}
            org = git >= 2 ? s[git - 2] : s[0];
            project = git >= 2 ? s[git - 1] : repo;
        }

        return new RemoteRepositoryRef(HostingProviderKind.AzureDevOps, host, org, repo, project);
    }

    private static bool TrySplit(string url, out string host, out string path)
    {
        host = path = string.Empty;

        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return false;
            host = uri.Host;
            path = uri.AbsolutePath;
            return true;
        }

        // scp-like: [user@]host:path
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return false;
        var left = url[..colon];
        var at = left.LastIndexOf('@');
        host = at >= 0 ? left[(at + 1)..] : left;
        path = url[(colon + 1)..];
        return host.Length > 0 && !host.Contains('\\', StringComparison.Ordinal) && host.Length > 1; // exclude "C:" drive paths
    }
}
