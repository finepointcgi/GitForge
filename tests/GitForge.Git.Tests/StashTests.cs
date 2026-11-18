using GitForge.Core.Diff;
using GitForge.Core.Models;

namespace GitForge.Git.Tests;

/// <summary>Stash preview (files and diffs of a stash) and stashing a hunk or line selection.</summary>
public class StashTests
{
    private static string Lines(params int[] edited) =>
        string.Concat(Enumerable.Range(1, 30).Select(i => edited.Contains(i) ? $"line {i} EDIT\n" : $"line {i}\n"));

    [Fact]
    public async Task StashLines_MovesOnlyTheSelection_AndPopBringsItBack()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("f.txt", Lines());
        await t.CommitAllAsync("base");
        t.Write("f.txt", Lines(2, 28));

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;
        Assert.Equal(2, diff.Hunks.Count);

        await repo.Stashes.SaveSelectionAsync(diff, PatchBuilder.SelectHunk(diff.Hunks[1]), "second hunk");

        Assert.Equal(Lines(2), File.ReadAllText(Path.Combine(t.Path, "f.txt")));
        var stash = Assert.Single((await repo.Read.GetSnapshotAsync()).Stashes);
        Assert.Contains("On main: second hunk", stash.Message, StringComparison.Ordinal);

        var contents = await repo.Stashes.GetContentsAsync(stash.Sha);
        var stashed = Assert.Single(contents.Files);
        Assert.False(stashed.Untracked);
        var stashDiff = (await repo.Stashes.GetDiffAsync(contents, stashed, new DiffOptions()))!;
        Assert.Contains(stashDiff.Hunks.SelectMany(h => h.Lines), l => l is { Kind: DiffLineKind.Added, Text: "line 28 EDIT" });
        Assert.DoesNotContain(stashDiff.Hunks.SelectMany(h => h.Lines), l => l.Text == "line 2 EDIT");

        await repo.Stashes.ApplyAsync(0, pop: true, restoreIndex: false);
        Assert.Equal(Lines(2, 28), File.ReadAllText(Path.Combine(t.Path, "f.txt")));
        Assert.Empty((await repo.Read.GetSnapshotAsync()).Stashes);
    }

    [Fact]
    public async Task Pop_IntoOverlappingEdits_ChangesNothing_AndKeepsTheStash()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("f.txt", Lines());
        await t.CommitAllAsync("base");
        t.Write("f.txt", Lines(2, 28));

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;
        await repo.Stashes.SaveSelectionAsync(diff, PatchBuilder.SelectHunk(diff.Hunks[1]), null);
        var edited = Lines(2).Replace("line 28\n", "line 28 OTHER\n", StringComparison.Ordinal);
        t.Write("f.txt", edited);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Stashes.ApplyAsync(0, pop: true, restoreIndex: false));
        Assert.Equal(edited, File.ReadAllText(Path.Combine(t.Path, "f.txt")));
        Assert.Single((await repo.Read.GetSnapshotAsync()).Stashes);
    }

    [Fact]
    public async Task StashLines_RefusesAFileWithStagedChanges()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("f.txt", Lines());
        await t.CommitAllAsync("base");
        t.Write("f.txt", Lines(2));
        await t.GitAsync("add", "f.txt");
        t.Write("f.txt", Lines(2, 28));

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Stashes.SaveSelectionAsync(diff, PatchBuilder.SelectHunk(diff.Hunks[0]), null));
        Assert.Empty((await repo.Read.GetSnapshotAsync()).Stashes);
        Assert.Equal(Lines(2, 28), File.ReadAllText(Path.Combine(t.Path, "f.txt")));
    }

    [Fact]
    public async Task StashPreview_ListsTrackedChangesAndUntrackedFiles()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "one\n");
        await t.CommitAllAsync("base");
        t.Write("a.txt", "one\ntwo\n");
        t.Write("u.txt", "brand new\n");
        await t.GitAsync("stash", "push", "--include-untracked", "-m", "both");

        var repo = await t.OpenAsync();
        var stash = Assert.Single((await repo.Read.GetSnapshotAsync()).Stashes);
        var contents = await repo.Stashes.GetContentsAsync(stash.Sha);

        Assert.Collection(contents.Files,
            f => Assert.Equal(new StashFile(new FileChange("a.txt", null, FileChangeKind.Modified), false), f),
            f => Assert.Equal(new StashFile(new FileChange("u.txt", null, FileChangeKind.Added), true), f));
        var untracked = (await repo.Stashes.GetDiffAsync(contents, contents.Files[1], new DiffOptions()))!;
        Assert.True(untracked.IsNewFile);
        Assert.Equal("brand new", untracked.Hunks.Single().Lines.Single().Text);
        var tracked = (await repo.Stashes.GetDiffAsync(contents, contents.Files[0], new DiffOptions()))!;
        Assert.Contains(tracked.Hunks.SelectMany(h => h.Lines), l => l is { Kind: DiffLineKind.Added, Text: "two" });
    }
}
