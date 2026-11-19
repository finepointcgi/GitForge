using GitForge.Core.Abstractions;
using GitForge.Core.Models;

namespace GitForge.Git.Tests;

/// <summary>Worktrees, patches, bundles, and bisect against real repositories.</summary>
public class WorkflowCommandTests
{
    [Fact]
    public async Task Worktree_AddListRemove()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "a\n");
        await t.CommitAllAsync("init");
        var repo = await t.OpenAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"gitforge-wt-{Guid.NewGuid():N}");
        try
        {
            await repo.Worktrees.AddAsync(folder, "main", "feature/x");

            var list = await repo.Worktrees.ListAsync();
            Assert.Equal(2, list.Count);
            Assert.True(list[0].IsMain && list[0].IsCurrent && list[0].Branch == "main");
            Assert.True(GitOutput.SamePath(folder, list[1].Path));
            Assert.Equal("feature/x", list[1].Branch);
            Assert.False(list[1].IsCurrent);
            Assert.True(File.Exists(Path.Combine(folder, "a.txt")));

            // Opened on its own, the worktree sees the main repository's remotes (libgit2 alone reports none).
            await t.GitAsync("remote", "add", "origin", "https://example.com/team/app.git");
            await t.GitAsync("update-ref", "refs/remotes/origin/main", "HEAD");
            await using (var linked = await new GitRepositoryFactory(TempRepo.Cli, TempRepo.Options).OpenAsync(folder))
            {
                var snapshot = await linked.Read.GetSnapshotAsync();
                Assert.Equal("origin", Assert.Single(snapshot.Remotes).Name);
                Assert.Equal("origin", Assert.Single(snapshot.RemoteBranches).RemoteName);
            }

            await repo.Worktrees.RemoveAsync(folder, force: false);
            Assert.Single(await repo.Worktrees.ListAsync());
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Patches_RoundTripCommitsAndUncommittedChanges()
    {
        await using var source = await TempRepo.CreateAsync();
        source.Write("a.txt", "one\r\ntwo\r\n");
        await source.CommitAllAsync("init");
        await using var target = await TempRepo.CreateAsync();
        await target.GitAsync("pull", "-q", source.Path, "main");

        source.Write("a.txt", "one\r\ntwo changed\r\n");
        await source.CommitAllAsync("change two");
        source.Write("b.bin", "\0\u0001binary");
        await source.CommitAllAsync("add binary");
        var repo = await source.OpenAsync();
        var shas = (await source.GitAsync("rev-list", "--reverse", "HEAD~2..HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dir = Directory.CreateTempSubdirectory("gitforge-patches-").FullName;
        try
        {
            var files = await repo.Transfer.SaveCommitsAsPatchesAsync(shas, dir);
            Assert.Equal(2, files.Count);
            Assert.StartsWith("0001-", Path.GetFileName(files[0]), StringComparison.Ordinal);

            var targetRepo = await target.OpenAsync();
            foreach (var f in files) (await targetRepo.Transfer.ApplyPatchAsync(f)).EnsureSuccess("am");
            Assert.Equal("add binary\nchange two\ninit", (await target.GitAsync("log", "--format=%s")).Trim().Replace("\r", string.Empty, StringComparison.Ordinal));
            Assert.Equal("one\r\ntwo changed\r\n", await File.ReadAllTextAsync(Path.Combine(target.Path, "a.txt")));

            // Uncommitted work: a staged edit and a new file, applied as one patch.
            source.Write("a.txt", "one edited\r\ntwo changed\r\n");
            await source.GitAsync("add", "a.txt");
            source.Write("new.txt", "brand new\n");
            var patchFile = Path.Combine(dir, "wip.patch");
            Assert.True(await repo.Transfer.SaveUncommittedPatchAsync(patchFile));
            (await targetRepo.Transfer.ApplyPatchAsync(patchFile)).EnsureSuccess("apply");
            Assert.Equal("one edited\r\ntwo changed\r\n", await File.ReadAllTextAsync(Path.Combine(target.Path, "a.txt")));
            Assert.Equal("brand new\n", await File.ReadAllTextAsync(Path.Combine(target.Path, "new.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Bundles_CarryBranchesAndIncrementalChanges()
    {
        await using var source = await TempRepo.CreateAsync();
        source.Write("a.txt", "1\n");
        await source.CommitAllAsync("one");
        await source.GitAsync("tag", "sent-1");
        await source.GitAsync("branch", "feature/y");
        var repo = await source.OpenAsync();
        var dir = Directory.CreateTempSubdirectory("gitforge-bundles-").FullName;
        await using var target = await TempRepo.CreateAsync();
        try
        {
            var full = Path.Combine(dir, "all.bundle");
            var refs = await repo.Transfer.CreateBundleAsync(full, ["--branches", "--tags"], null);
            Assert.Contains("refs/heads/main", refs);
            Assert.Contains("refs/heads/feature/y", refs);

            var targetRepo = await target.OpenAsync();
            await targetRepo.Transfer.FetchBundleAsync(full, "bundle", null);
            Assert.Equal((await source.GitAsync("rev-parse", "main")).Trim(), (await target.GitAsync("rev-parse", "bundle/main")).Trim());
            Assert.Contains("bundle/feature/y", await target.GitAsync("branch", "-r"));

            // Only what changed since the last transfer; the receiving side already has sent-1.
            source.Write("a.txt", "2\n");
            await source.CommitAllAsync("two");
            var incremental = Path.Combine(dir, "since.bundle");
            await repo.Transfer.CreateBundleAsync(incremental, ["main"], "sent-1");
            await targetRepo.Transfer.FetchBundleAsync(incremental, "bundle", null);
            Assert.Equal("two", (await target.GitAsync("log", "-1", "--format=%s", "bundle/main")).Trim());

            // A repository without the basis commit is told what is missing instead of getting a partial history.
            await using var empty = await TempRepo.CreateAsync();
            var emptyRepo = await empty.OpenAsync();
            var ex = await Assert.ThrowsAsync<GitCommandException>(() => emptyRepo.Transfer.FetchBundleAsync(incremental, "bundle", null));
            Assert.Contains("prerequisite", ex.GitMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Bisect_FindsTheFirstBadCommit()
    {
        await using var t = await TempRepo.CreateAsync();
        var shas = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            t.Write("n.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (i == 6) t.Write("bug.txt", "broken");
            await t.CommitAllAsync($"c{i}");
            shas.Add((await t.GitAsync("rev-parse", "HEAD")).Trim());
        }

        var repo = await t.OpenAsync();
        await repo.History.StartBisectAsync("HEAD", BisectMark.Bad);
        var waiting = await repo.History.GetBisectStatusAsync();
        Assert.True(waiting.NeedsGood);
        Assert.Equal(shas[^1], waiting.Bad);

        var output = (await repo.History.MarkBisectAsync(BisectMark.Good, shas[0])).StandardOutput;
        var status = await repo.History.GetBisectStatusAsync();
        Assert.False(status.NeedsGood);
        Assert.Equal(7, status.Remaining); // c2..c8 are left to test
        for (var steps = 0; GitOutput.FirstBadCommit(output) is null; steps++)
        {
            Assert.True(steps < 6, "bisect did not converge");
            var bad = File.Exists(Path.Combine(t.Path, "bug.txt"));
            output = (await repo.History.MarkBisectAsync(bad ? BisectMark.Bad : BisectMark.Good)).StandardOutput;
        }

        Assert.Equal(shas[5], GitOutput.FirstBadCommit(output));
        Assert.Equal(shas[5], (await repo.History.GetBisectStatusAsync()).FirstBad);

        await repo.History.ResetBisectAsync();
        Assert.Equal(BisectStatus.None, await repo.History.GetBisectStatusAsync());
        Assert.Equal("main", (await t.GitAsync("branch", "--show-current")).Trim());
    }
}
