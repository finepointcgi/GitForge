using GitForge.Core.Hosting;
using GitForge.Core.Models;
using GitForge.Core.Rebase;
using GitForge.Core.Validation;
using GitForge.Hosting;

namespace GitForge.Core.Tests;

public class RemoteUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/octo/hello.git", HostingProviderKind.GitHub, "octo", "hello", null)]
    [InlineData("git@github.com:octo/hello.git", HostingProviderKind.GitHub, "octo", "hello", null)]
    [InlineData("ssh://git@github.com/octo/hello", HostingProviderKind.GitHub, "octo", "hello", null)]
    [InlineData("https://gitlab.com/group/sub/proj.git", HostingProviderKind.GitLab, "group/sub", "proj", null)]
    [InlineData("https://user@bitbucket.org/ws/repo.git", HostingProviderKind.Bitbucket, "ws", "repo", null)]
    [InlineData("https://org@dev.azure.com/org/My%20Project/_git/repo", HostingProviderKind.AzureDevOps, "org", "repo", "My Project")]
    [InlineData("git@ssh.dev.azure.com:v3/org/proj/repo", HostingProviderKind.AzureDevOps, "org", "repo", "proj")]
    [InlineData("https://org.visualstudio.com/DefaultCollection/proj/_git/repo", HostingProviderKind.AzureDevOps, "org", "repo", "proj")]
    public void ParsesCloudRemotes(string url, HostingProviderKind kind, string owner, string repo, string? project)
    {
        var r = RemoteUrlParser.Parse(url)!;
        Assert.Equal(kind, r.Provider);
        Assert.Equal(owner, r.Owner);
        Assert.Equal(repo, r.Repository);
        Assert.Equal(project, r.Project);
    }

    [Fact]
    public void SelfHostedRequiresResolver()
    {
        const string url = "https://gitlab.example.mil/team/app.git";
        Assert.Null(RemoteUrlParser.Parse(url));
        var r = RemoteUrlParser.Parse(url, h => h == "gitlab.example.mil" ? HostingProviderKind.GitLab : null)!;
        Assert.Equal("team/app", r.FullName);
    }

    [Theory]
    [InlineData("C:\\repos\\local")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("")]
    public void LocalPathsAreNotHosted(string url) => Assert.Null(RemoteUrlParser.Parse(url));
}

public class RefNameValidatorTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/ABC-123-thing")]
    [InlineData("release/1.2.3")]
    public void AcceptsValidNames(string name) => Assert.Null(RefNameValidator.Validate(name));

    [Theory]
    [InlineData("-rf")]
    [InlineData("a..b")]
    [InlineData("has space")]
    [InlineData("ends.lock")]
    [InlineData("x/.hidden")]
    [InlineData("trailing/")]
    [InlineData("a@{b")]
    [InlineData("star*")]
    [InlineData("")]
    public void RejectsInvalidNames(string name) => Assert.NotNull(RefNameValidator.Validate(name));
}

public class RebaseTodoWriterTests
{
    private const string A = "aaaaaaaa11111111";
    private const string B = "bbbbbbbb22222222";

    [Fact]
    public void WritesActionsAndRewordExec()
    {
        var todo = RebaseTodoWriter.Write(
            [new(A, "first", RebaseAction.Reword, "Better message"), new(B, "second", RebaseAction.Fixup)],
            _ => "C:\\tmp\\msg it's.txt");

        Assert.Equal(
            $"pick {A}\nexec git commit --amend --only --allow-empty -F 'C:/tmp/msg it'\\''s.txt'\nfixup {B}\n",
            todo);
    }

    [Fact]
    public void RejectsLeadingSquash() =>
        Assert.NotNull(RebaseTodoWriter.Validate([new(A, "x", RebaseAction.Drop), new(B, "y", RebaseAction.Squash)]));

    [Fact]
    public void RejectsNonHexSha() =>
        Assert.Throws<InvalidOperationException>(() => RebaseTodoWriter.Write([new("HEAD; rm -rf /", "x")], _ => "m"));
}

public class HostingDefaultsTests
{
    [Fact]
    public void GitHubEnterpriseUsesApiV3()
    {
        var a = HostingDefaults.CreateAccount(HostingProviderKind.GitHub, "Work", "me", "https://ghe.example.com");
        Assert.Equal("https://ghe.example.com/api/v3", a.ApiBaseUrl.ToString().TrimEnd('/'));
        Assert.True(a.IsSelfHosted);
    }

    [Fact]
    public void AzureCloudRequiresOrganization()
    {
        Assert.Throws<ArgumentException>(() => HostingDefaults.CreateAccount(HostingProviderKind.AzureDevOps, "x", null));
        var a = HostingDefaults.CreateAccount(HostingProviderKind.AzureDevOps, "x", null, organization: "contoso");
        Assert.Equal("https://dev.azure.com/contoso", a.ApiBaseUrl.ToString().TrimEnd('/'));
    }

    [Fact]
    public void AcceptsPlainHttpForSelfHosted()
    {
        var a = HostingDefaults.CreateAccount(HostingProviderKind.GitLab, "x", null, "http://gitlab.local:8080");
        Assert.Equal("http://gitlab.local:8080/api/v4", a.ApiBaseUrl.ToString().TrimEnd('/'));
        Assert.True(a.IsInsecureHttp);
        Assert.False(HostingDefaults.CreateAccount(HostingProviderKind.GitLab, "x", null, "https://gitlab.local").IsInsecureHttp);
    }

    [Theory]
    [InlineData("ftp://gitlab.local")]
    [InlineData("file:///c:/repos")]
    [InlineData("gitlab.local")]
    public void RejectsNonWebSchemes(string url) =>
        Assert.Throws<ArgumentException>(() => HostingDefaults.CreateAccount(HostingProviderKind.GitLab, "x", null, url));

    [Fact]
    public void RequestPolicy_AllowsHttpOnlyForHttpAccountsOnTheSameHostAndPort()
    {
        var http = HostingDefaults.CreateAccount(HostingProviderKind.GitLab, "x", null, "http://gitlab.local:8080");
        var https = HostingDefaults.CreateAccount(HostingProviderKind.GitLab, "x", null, "https://gitlab.local");

        Assert.True(HostingDefaults.IsRequestAllowed(new Uri("http://gitlab.local:8080/api/v4/user"), http));
        Assert.True(HostingDefaults.IsRequestAllowed(new Uri("https://gitlab.local/api/v4/user"), http)); // upgrading is always fine
        Assert.False(HostingDefaults.IsRequestAllowed(new Uri("http://evil.example/api/v4/user"), http)); // no cross-host HTTP hop
        Assert.False(HostingDefaults.IsRequestAllowed(new Uri("http://gitlab.local:9090/api/v4/user"), http)); // nor another port
        Assert.False(HostingDefaults.IsRequestAllowed(new Uri("http://gitlab.local/api/v4/user"), https)); // HTTPS accounts never downgrade
    }
}
