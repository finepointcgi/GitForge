using GitForge.Core.Models;
using GitForge.Git.Engine;
using GitForge.Git.Submodules;

namespace GitForge.Git.Tests;

/// <summary>Runs alone: nested local clones plus process-wide GIT_CONFIG_* environment variables.</summary>
[CollectionDefinition("Submodules", DisableParallelization = true)]
public sealed class SubmoduleSerialGroup;

/// <summary>Real nested submodules: super → libs/sub → nested.</summary>
[Collection("Submodules")]
public class SubmoduleTests
{
    static SubmoduleTests()
    {
        // Git 2.38.1+ blocks file:// submodule transport by default (CVE-2022-39253). Allow it for local test fixtures only,
        // via environment config that child git processes inherit.
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TempRepo Nested { get; init; }
        public required TempRepo Sub { get; init; }
        public required TempRepo Super { get; init; }
        public required string SubC1 { get; init; }
        public required string SubC2 { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Super.DisposeAsync();
            await Sub.DisposeAsync();
            await Nested.DisposeAsync();
        }
    }

    /// <summary>
    /// Super HEAD pins libs/sub at C1 (which contains the nested submodule); HEAD~1 pins it at C2.
    /// </summary>
    private static async Task<Fixture> CreateAsync()
    {
        var nested = await TempRepo.CreateAsync();
        nested.Write("deep.txt", "deep\n");
        await nested.CommitAllAsync("nested init");

        var sub = await TempRepo.CreateAsync();
        sub.Write("sub.txt", "v1\n");
        await sub.GitAsync("submodule", "add", "-q", nested.Path, "nested");
        await sub.CommitAllAsync("sub c1 with nested");
        var c1 = (await sub.GitAsync("rev-parse", "HEAD")).Trim();
        sub.Write("sub.txt", "v2\n");
        await sub.CommitAllAsync("sub c2");
        var c2 = (await sub.GitAsync("rev-parse", "HEAD")).Trim();

        var super = await TempRepo.CreateAsync();
        super.Write("app.txt", "app\n");
        await super.CommitAllAsync("init");
        await super.GitAsync("submodule", "add", "-q", sub.Path, "libs/sub");
        await super.CommitAllAsync("add sub at c2");
        await super.GitAsync("-C", "libs/sub", "checkout", "-q", c1);
        await super.CommitAllAsync("pin sub to c1");
        await super.GitAsync("submodule", "update", "--init", "--recursive", "-q"); // nested exists at c1

        return new Fixture { Nested = nested, Sub = sub, Super = super, SubC1 = c1, SubC2 = c2 };
    }

    [Fact]
    public async Task AfterCheckout_UpdatesChangedSubmoduleRecursively()
    {
        await using var f = await CreateAsync();
        var repo = await f.Super.OpenAsync();
        var before = SubmoduleAutoUpdater.Capture(await repo.Read.GetSnapshotAsync());

        await repo.Branches.CheckoutAsync("HEAD~1"); // records sub@c2; plain checkout leaves the submodule at c1
        Assert.Equal(f.SubC1, (await f.Super.GitAsync("-C", "libs/sub", "rev-parse", "HEAD")).Trim());

        var result = await SubmoduleAutoUpdater.UpdateChangedAsync(repo, before, null);

        Assert.Equal(["libs/sub"], result.Updated);
        Assert.Equal(f.SubC2, (await f.Super.GitAsync("-C", "libs/sub", "rev-parse", "HEAD")).Trim());
        Assert.True(File.Exists(Path.Combine(f.Super.Path, "libs", "sub", "nested", "deep.txt")), "nested submodule should be checked out");
    }

    [Fact]
    public async Task DirtySubmodule_IsSkipped_NotOverwritten()
    {
        await using var f = await CreateAsync();
        var repo = await f.Super.OpenAsync();
        var before = SubmoduleAutoUpdater.Capture(await repo.Read.GetSnapshotAsync());
        File.WriteAllText(Path.Combine(f.Super.Path, "libs", "sub", "sub.txt"), "local work\n");

        await repo.Branches.CheckoutAsync("HEAD~1");
        var result = await SubmoduleAutoUpdater.UpdateChangedAsync(repo, before, null);

        Assert.Empty(result.Updated);
        Assert.Equal(["libs/sub"], result.SkippedDirty);
        Assert.Equal("local work\n", File.ReadAllText(Path.Combine(f.Super.Path, "libs", "sub", "sub.txt")));
    }

    [Fact]
    public async Task UnchangedHead_DoesNothing()
    {
        await using var f = await CreateAsync();
        var repo = await f.Super.OpenAsync();
        var before = SubmoduleAutoUpdater.Capture(await repo.Read.GetSnapshotAsync());

        var result = await SubmoduleAutoUpdater.UpdateChangedAsync(repo, before, null);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task FreshClone_InitializesMissingSubmodulesRecursively()
    {
        await using var f = await CreateAsync();
        var cloneDir = Path.Combine(Path.GetTempPath(), "gitforge-clone-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var factory = new GitRepositoryFactory(TempRepo.Cli, TempRepo.Options);
            await factory.CloneAsync(f.Super.Path, cloneDir, recurseSubmodules: false, null);
            await using var repo = await factory.OpenAsync(cloneDir);
            Assert.Contains((await repo.Read.GetSnapshotAsync()).Submodules, s => s.State.HasFlag(SubmoduleState.NotInitialized));

            var result = await SubmoduleAutoUpdater.InitializeMissingAsync(repo, null);

            Assert.Equal(["libs/sub"], result.Updated);
            Assert.True(File.Exists(Path.Combine(cloneDir, "libs", "sub", "nested", "deep.txt")));
            var status = await repo.Submodules.GetRecursiveStatusAsync();
            Assert.Contains(status, s => s is { Path: "libs/sub", Depth: 0, State: SubmoduleSyncState.Current });
            Assert.Contains(status, s => s is { Path: "libs/sub/nested", Depth: 1, State: SubmoduleSyncState.Current });
        }
        finally
        {
            TryDelete(cloneDir);
        }
    }

    [Fact]
    public void ParsesRecursiveStatus()
    {
        const string output =
            " 1111111111111111111111111111111111111111 libs/sub (heads/main)\n" +
            "+2222222222222222222222222222222222222222 libs/sub/nested (v1.0-3-g2222222)\n" +
            "-3333333333333333333333333333333333333333 tools/my tool\n" +
            "U4444444444444444444444444444444444444444 vendor/x\n";

        var entries = SubmoduleOperations.ParseStatus(output);

        Assert.Equal(4, entries.Count);
        Assert.Equal(("libs/sub", SubmoduleSyncState.Current, 0, "heads/main"), (entries[0].Path, entries[0].State, entries[0].Depth, entries[0].Describe));
        Assert.Equal(("libs/sub/nested", SubmoduleSyncState.OutOfSync, 1), (entries[1].Path, entries[1].State, entries[1].Depth));
        Assert.Equal(("tools/my tool", SubmoduleSyncState.NotInitialized, (string?)null), (entries[2].Path, entries[2].State, entries[2].Describe));
        Assert.Equal(SubmoduleSyncState.Conflicted, entries[3].State);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
