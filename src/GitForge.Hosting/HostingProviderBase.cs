using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Hosting;

public sealed class HostingApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Shared HTTP plumbing: HTTPS enforcement, auth headers, JSON, and pagination.</summary>
public abstract class HostingProviderBase(IHttpClientFactory httpClientFactory) : IHostingProvider
{
    public const string HttpClientName = "gitforge-hosting";
    protected const string UserAgent = "GitForge/0.1";

    public abstract HostingProviderKind Kind { get; }
    public abstract Task<HostingUser> ValidateAsync(HostingAccount account, string token, CancellationToken cancellationToken = default);
    public abstract IAsyncEnumerable<HostedRepository> ListRepositoriesAsync(HostingAccount account, string token, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<HostedPullRequest>> ListPullRequestsAsync(HostingAccount account, string token, RemoteRepositoryRef repository, CancellationToken cancellationToken = default);
    public abstract string GetCreatePullRequestUrl(HostingAccount? account, RemoteRepositoryRef repository, string sourceBranch, string targetBranch);

    /// <summary>Sets provider-specific auth and media-type headers.</summary>
    protected abstract void Authorize(HttpRequestMessage request, HostingAccount account, string token);

    protected async Task<(JsonDocument Json, HttpResponseMessage Response)> GetAsync(Uri uri, HostingAccount account, string token, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException($"Refusing non-HTTPS API call to {uri.Host}.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        Authorize(request, account, token);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HostingApiException(status, status switch
            {
                HttpStatusCode.Unauthorized => $"{Kind}: authentication failed. Check the token and its expiry.",
                HttpStatusCode.Forbidden => $"{Kind}: access denied. The token may be missing required scopes, or a rate limit was hit.",
                HttpStatusCode.NotFound => $"{Kind}: resource not found ({uri.AbsolutePath}).",
                _ => $"{Kind}: request failed with HTTP {(int)status}.",
            });
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return (json, response);
    }

    /// <summary>RFC 8288 Link header "next" relation (GitHub, GitLab).</summary>
    protected static Uri? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(',', values).Split(','))
        {
            var segments = part.Split(';');
            if (segments.Length < 2 || !segments.Skip(1).Any(s => s.Trim() is "rel=\"next\"" or "rel=next")) continue;
            var url = segments[0].Trim().TrimStart('<').TrimEnd('>');
            return Uri.TryCreate(url, UriKind.Absolute, out var next) ? next : null;
        }

        return null;
    }

    protected static void Bearer(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    protected static void Basic(HttpRequestMessage request, string user, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));

    protected static Uri Combine(Uri baseUri, string relative) =>
        new($"{baseUri.ToString().TrimEnd('/')}/{relative.TrimStart('/')}");

    protected static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        };
    }

    protected static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    protected static DateTimeOffset? Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Escapes a branch for a URL path while keeping '/' readable.</summary>
    protected static string EscapeBranch(string branch) => Uri.EscapeDataString(branch).Replace("%2F", "/", StringComparison.Ordinal);
}
