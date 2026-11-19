using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Git.Tests;

/// <summary>Adding, removing, and moving submodules, and the push check, against real repositories and bare remotes.</summary>
[Collection("Submodules")]
public class SubmoduleCommandTests
{
    static SubmoduleCommandTests()
    {
        // Same as SubmoduleTests: allow file:// submodules for local fixtures only.
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    private static async Task<string> BareAsync()
    {
        var dir = Directory.CreateTempSubdirectory("gitforge-bare-").FullName;
        (await TempRepo.Cli.RunAsync(new GitCommand(dir, ["init", "--bare", "-q", "--initial-branch=main"]))).EnsureSuccess("init");
        return dir;
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task AddThenRemove_StagesBothChanges()
    {
        await using var lib = await TempRepo.CreateAsync();
        lib.Write("lib.txt", "lib\n");
        await lib.CommitAllAsync("lib");
        await using var super = await TempRepo.CreateAsync();
        super.Write("app.txt", "app\n");
        await super.CommitAllAsync("init");
        var repo = await super.OpenAsync();

        await repo.Submodules.AddAsync(lib.Path, "libs/lib", null, null);
        Assert.True(File.Exists(Path.Combine(super.Path, "libs", "lib", "lib.txt")));
        var staged = await super.GitAsync("diff", "--cached", "--name-only");
        Assert.Contains(".gitmodules", staged);
        Assert.Contains("libs/lib", staged);
        await super.GitAsync("commit", "-q", "-m", "add lib");

        await repo.Submodules.RemoveAsync("libs/lib");
        Assert.False(Directory.Exists(Path.Combine(super.Path, "libs", "lib")) && Directory.EnumerateFileSystemEntries(Path.Combine(super.Path, "libs", "lib")).Any());
        Assert.Contains("libs/lib", await super.GitAsync("diff", "--cached", "--name-only"));
        var gitmodules = Path.Combine(super.Path, ".gitmodules");
        Assert.DoesNotContain("libs/lib", File.Exists(gitmodules) ? await File.ReadAllTextAsync(gitmodules) : string.Empty);
    }

    [Fact]
    public async Task Worktree_WithSubmodules_IsRemovedByDeletingAndPruning_OnlyWhenForced()
    {
        await using var lib = await TempRepo.CreateAsync();
        lib.Write("lib.txt", "lib\n");
        await lib.CommitAllAsync("lib");
        await using var super = await TempRepo.CreateAsync();
        super.Write("app.txt", "app\n");
        await super.CommitAllAsync("init");
        await super.GitAsync("submodule", "add", "-q", lib.Path, "libs/lib");
        await super.CommitAllAsync("add lib");
        var repo = await super.OpenAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"gitforge-wt-{Guid.NewGuid():N}");
        try
        {
            await repo.Worktrees.AddAsync(folder, "main", "side");
            await super.GitAsync("-C", folder, "submodule", "update", "--init", "-q");

            var refused = await Assert.ThrowsAsync<GitCommandException>(() => repo.Worktrees.RemoveAsync(folder, force: false));
            Assert.Contains("containing submodules", refused.GitMessage, StringComparison.Ordinal);
            Assert.True(Directory.Exists(folder));

            await repo.Worktrees.RemoveAsync(folder, force: true);
            Assert.False(Directory.Exists(folder));
            Assert.Single(await repo.Worktrees.ListAsync());
            Assert.Contains("side", await super.GitAsync("branch", "--list", "side")); // the branch stays

            // The main worktree is never deleted this way.
            await Assert.ThrowsAnyAsync<Exception>(() => repo.Worktrees.RemoveAsync(super.Path, force: true));
            Assert.True(File.Exists(Path.Combine(super.Path, "app.txt")));
        }
        finally
        {
            DeleteTree(folder);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/abs")]
    [InlineData("-x")]
    [InlineData("a/../b")]
    public async Task Add_RejectsPathsOutsideTheRepository(string path)
    {
        await using var super = await TempRepo.CreateAsync();
        var repo = await super.OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => repo.Submodules.AddAsync("https://example.com/x.git", path, null, null));
    }

    [Fact]
    public async Task UpdateToRemote_MovesToNewestCommit_AndUpdateOne_ReachesNestedSubmodules()
    {
        await using var nested = await TempRepo.CreateAsync();
        nested.Write("deep.txt", "deep\n");
        await nested.CommitAllAsync("nested");
        await using var lib = await TempRepo.CreateAsync();
        lib.Write("lib.txt", "v1\n");
        await lib.GitAsync("submodule", "add", "-q", nested.Path, "nested");
        await lib.CommitAllAsync("lib v1");
        await using var super = await TempRepo.CreateAsync();
        super.Write("app.txt", "app\n");
        await super.CommitAllAsync("init");
        await super.GitAsync("submodule", "add", "-q", lib.Path, "libs/lib");
        await super.GitAsync("submodule", "update", "--init", "--recursive", "-q");
        await super.CommitAllAsync("add lib");
        var repo = await super.OpenAsync();

        lib.Write("lib.txt", "v2\n");
        await lib.CommitAllAsync("lib v2");
        var v2 = (await lib.GitAsync("rev-parse", "HEAD")).Trim();

        await repo.Submodules.UpdateToRemoteAsync(["libs/lib"], null);
        Assert.Equal(v2, (await super.GitAsync("-C", "libs/lib", "rev-parse", "HEAD")).Trim());
        Assert.Contains("libs/lib", await super.GitAsync("diff", "--name-only")); // ready to stage and commit
        Assert.True(File.Exists(Path.Combine(super.Path, "libs", "lib", "nested", "deep.txt")));

        // A nested submodule moved by hand comes back to the commit its parent records.
        var nestedPin = (await super.GitAsync("-C", "libs/lib/nested", "rev-parse", "HEAD")).Trim();
        nested.Write("deep.txt", "deeper\n");
        await nested.CommitAllAsync("nested 2");
        await super.GitAsync("-C", "libs/lib/nested", "fetch", "-q", "origin");
        await super.GitAsync("-C", "libs/lib/nested", "checkout", "-q", "origin/main");
        await repo.Submodules.UpdateOneAsync("libs/lib/nested", null);
        Assert.Equal(nestedPin, (await super.GitAsync("-C", "libs/lib/nested", "rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task Push_RefusesUnpushedSubmoduleCommits_UntilPushedOnDemand()
    {
        var libOrigin = await BareAsync();
        var superOrigin = await BareAsync();
        try
        {
            await using var lib = await TempRepo.CreateAsync();
            lib.Write("lib.txt", "v1\n");
            await lib.CommitAllAsync("lib v1");
            await lib.GitAsync("push", "-q", libOrigin, "main");

            await using var super = await TempRepo.CreateAsync();
            super.Write("app.txt", "app\n");
            await super.CommitAllAsync("init");
            await super.GitAsync("submodule", "add", "-q", libOrigin, "libs/lib");
            await super.CommitAllAsync("add lib");
            await super.GitAsync("remote", "add", "origin", superOrigin);
            await super.GitAsync("push", "-q", "-u", "origin", "main");

            // A commit inside the submodule that only exists here, recorded by the superproject.
            var sub = Path.Combine(super.Path, "libs", "lib");
            await super.GitAsync("-C", sub, "config", "user.name", "Test User");
            await super.GitAsync("-C", sub, "config", "user.email", "test@example.com");
            await super.GitAsync("-C", sub, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(sub, "lib.txt"), "local\n");
            await super.GitAsync("-C", sub, "commit", "-q", "-am", "local lib change");
            var local = (await super.GitAsync("-C", sub, "rev-parse", "HEAD")).Trim();
            await super.CommitAllAsync("use local lib change");
            var repo = await super.OpenAsync();

            var ex = await Assert.ThrowsAsync<GitCommandException>(() => repo.Remotes.PushAsync(new PushRequest("origin", "main"), null));
            Assert.Equal(["libs/lib"], GitOutput.SubmodulesMissingOnRemote(ex.GitMessage));

            await repo.Remotes.PushAsync(new PushRequest("origin", "main", Submodules: SubmodulePushMode.OnDemand), null);
            var onLibRemote = await TempRepo.Cli.RunAsync(new GitCommand(libOrigin, ["cat-file", "-e", local + "^{commit}"]));
            Assert.True(onLibRemote.Succeeded, "the submodule commit should have been pushed first");
            Assert.Equal((await super.GitAsync("rev-parse", "HEAD")).Trim(),
                (await TempRepo.Cli.RunAsync(new GitCommand(superOrigin, ["rev-parse", "main"]))).StandardOutput.Trim());
        }
        finally
        {
            DeleteTree(libOrigin);
            DeleteTree(superOrigin);
        }
    }

    [Fact]
    public async Task PushRecordedCommit_PushesTheBranchTheCommitIsOn_AndExplainsDetachedCommits()
    {
        var libOrigin = await BareAsync();
        var superOrigin = await BareAsync();
        try
        {
            await using var lib = await TempRepo.CreateAsync();
            lib.Write("lib.txt", "v1\n");
            await lib.CommitAllAsync("lib v1");
            await lib.GitAsync("push", "-q", libOrigin, "main");

            await using var super = await TempRepo.CreateAsync();
            super.Write("app.txt", "app\n");
            await super.CommitAllAsync("init");
            await super.GitAsync("submodule", "add", "-q", libOrigin, "libs/lib");
            await super.CommitAllAsync("add lib");
            await super.GitAsync("remote", "add", "origin", superOrigin);
            await super.GitAsync("push", "-q", "-u", "origin", "main");
            var sub = Path.Combine(super.Path, "libs", "lib");
            foreach (var (k, v) in new[] { ("user.name", "Test User"), ("user.email", "test@example.com"), ("commit.gpgsign", "false") }) await super.GitAsync("-C", sub, "config", k, v);
            var repo = await super.OpenAsync();

            // Committed on a detached HEAD: nothing to push, and the message says what to do.
            await super.GitAsync("-C", sub, "checkout", "-q", "--detach");
            File.WriteAllText(Path.Combine(sub, "lib.txt"), "detached\n");
            await super.GitAsync("-C", sub, "commit", "-q", "-am", "detached change");
            await super.CommitAllAsync("use detached change");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Submodules.PushRecordedCommitAsync("libs/lib", "main", null));
            Assert.Contains("detached HEAD", ex.Message, StringComparison.Ordinal);

            // On a branch named unlike the superproject's (git's on-demand push would push the submodule's main instead).
            await super.GitAsync("-C", sub, "checkout", "-q", "-b", "local-work");
            Assert.Equal("origin/local-work", await repo.Submodules.PushRecordedCommitAsync("libs/lib", "main", null));
            await repo.Remotes.PushAsync(new PushRequest("origin", "main"), null); // the check now passes
            Assert.Equal((await super.GitAsync("-C", sub, "rev-parse", "HEAD")).Trim(),
                (await TempRepo.Cli.RunAsync(new GitCommand(libOrigin, ["rev-parse", "local-work"]))).StandardOutput.Trim());
        }
        finally
        {
            DeleteTree(libOrigin);
            DeleteTree(superOrigin);
        }
    }
}
