using GitForge.Core.Merge;
using GitForge.Core.Models;

namespace GitForge.Git.Tests;

public class ConflictEditorTests
{
    /// <summary>main and topic both edit line 2; topic also edits line 9 (merges cleanly).</summary>
    private static async Task<TempRepo> ConflictedRepoAsync()
    {
        var t = await TempRepo.CreateAsync();
        t.Write("f.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        await t.CommitAllAsync("base");
        await t.GitAsync("checkout", "-q", "-b", "topic");
        t.Write("f.txt", "1\nTWO-theirs\n3\n4\n5\n6\n7\n8\nNINE-theirs\n10\n");
        await t.CommitAllAsync("topic edits");
        await t.GitAsync("checkout", "-q", "main");
        t.Write("f.txt", "1\nTWO-mine\n3\n4\n5\n6\n7\n8\n9\n10\n");
        await t.CommitAllAsync("main edits");
        await Assert.ThrowsAsync<GitForge.Core.Abstractions.GitCommandException>(() => t.GitAsync("merge", "-q", "topic"));
        return t;
    }

    [Fact]
    public async Task MergeText_HasOneConflict_AndCleanChangesMerged()
    {
        await using var t = await ConflictedRepoAsync();
        var repo = await t.OpenAsync();

        var sides = await repo.Index.GetConflictSidesAsync("f.txt");
        Assert.Contains("TWO-mine", sides.Ours);
        Assert.Contains("TWO-theirs", sides.Theirs);
        Assert.False(sides.IsDeleteConflict);

        var doc = MergeDocument.Parse((await repo.Index.GetMergeTextAsync("f.txt"))!);
        var conflict = Assert.Single(doc.Conflicts);
        Assert.Equal(["TWO-mine"], conflict.Ours);
        Assert.Equal(["2"], conflict.Base);
        Assert.Equal(["TWO-theirs"], conflict.Theirs);
        Assert.Contains(doc.Segments.OfType<MergeCommon>().SelectMany(c => c.Lines), l => l == "NINE-theirs");
    }

    [Fact]
    public async Task SaveResolution_WritesFile_AndClearsConflict()
    {
        await using var t = await ConflictedRepoAsync();
        var repo = await t.OpenAsync();
        var doc = MergeDocument.Parse((await repo.Index.GetMergeTextAsync("f.txt"))!);

        doc.Conflicts[0].Resolution = ConflictResolution.OursThenTheirs;
        await repo.Index.SaveResolutionAsync("f.txt", doc.Render());

        Assert.Equal("1\nTWO-mine\nTWO-theirs\n3\n4\n5\n6\n7\n8\nNINE-theirs\n10\n", File.ReadAllText(Path.Combine(t.Path, "f.txt")));
        var status = await repo.Read.GetStatusAsync();
        Assert.Empty(status.Conflicted);
        Assert.Contains(status.Staged, f => f.Path == "f.txt");
    }

    [Fact]
    public async Task ModifyDeleteConflict_IsReportedAndResolvable()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("gone.txt", "a\n");
        await t.CommitAllAsync("base");
        await t.GitAsync("checkout", "-q", "-b", "topic");
        await t.GitAsync("rm", "-q", "gone.txt");
        await t.CommitAllAsync("delete");
        await t.GitAsync("checkout", "-q", "main");
        t.Write("gone.txt", "a changed\n");
        await t.CommitAllAsync("modify");
        await Assert.ThrowsAsync<GitForge.Core.Abstractions.GitCommandException>(() => t.GitAsync("merge", "-q", "topic"));
        var repo = await t.OpenAsync();

        Assert.True((await repo.Index.GetConflictSidesAsync("gone.txt")).IsDeleteConflict);
        Assert.Null(await repo.Index.GetMergeTextAsync("gone.txt"));

        await repo.Index.ResolveDeletionAsync("gone.txt", keepFile: false);
        Assert.False(File.Exists(Path.Combine(t.Path, "gone.txt")));
        Assert.Empty((await repo.Read.GetStatusAsync()).Conflicted);
    }
}
