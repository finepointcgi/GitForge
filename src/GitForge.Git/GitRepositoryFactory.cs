using GitForge.Core.Abstractions;
using GitForge.Git.Cli;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;

namespace GitForge.Git;

public sealed class GitRepositoryFactory(IGitCli cli, GitEngineOptions options) : IGitRepositoryFactory
{
    public bool IsRepository(string path) => Directory.Exists(path) && Repository.IsValid(path);

    public string? Discover(string startPath)
    {
        if (!Directory.Exists(startPath)) return null;
        var gitDir = Repository.Discover(startPath);
        if (gitDir is null) return null;
        using var repo = new Repository(gitDir);
        return (repo.Info.WorkingDirectory ?? repo.Info.Path).TrimEnd('/', '\\');
    }

    public Task<IGitRepository> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run<IGitRepository>(() =>
        {
            var gitDir = Repository.Discover(path) ?? throw new RepositoryNotFoundException($"'{path}' is not inside a Git repository.");
            using var probe = new Repository(gitDir);
            if (probe.Info.IsBare) throw new NotSupportedException("Bare repositories are not supported in the working-copy UI.");
            var workDir = probe.Info.WorkingDirectory.TrimEnd('/', '\\');
            return new GitRepository(workDir, probe.Info.Path, cli, options);
        }, cancellationToken);

    public async Task<string> CloneAsync(string url, string destination, bool recurseSubmodules, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('-')) throw new ArgumentException("Invalid clone URL.", nameof(url));
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new ArgumentException("Invalid destination.", nameof(destination));
        Directory.CreateDirectory(parent);

        var args = new List<string> { "clone", "--progress" };
        if (recurseSubmodules) args.Add("--recurse-submodules");
        args.AddRange(["--", url, destination]);
        var result = await cli.RunAsync(new GitCommand(parent, args) { Progress = progress }, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("clone");
        return Path.GetFullPath(destination);
    }

    public async Task<string> InitAsync(string path, string? initialBranch = "main", CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(path);
        var args = new List<string> { "init" };
        if (!string.IsNullOrWhiteSpace(initialBranch)) args.Add($"--initial-branch={initialBranch}");
        args.AddRange(["--", "."]);
        (await cli.RunAsync(new GitCommand(path, args), cancellationToken).ConfigureAwait(false)).EnsureSuccess("init");
        return Path.GetFullPath(path);
    }
}

public static class GitServiceCollectionExtensions
{
    public static IServiceCollection AddGitForgeGit(this IServiceCollection services)
    {
        services.AddSingleton<GitEngineOptions>();
        services.AddSingleton<IGitCli, GitCli>();
        services.AddSingleton<IGitRepositoryFactory, GitRepositoryFactory>();
        return services;
    }
}
