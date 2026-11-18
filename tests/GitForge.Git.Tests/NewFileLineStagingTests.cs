using GitForge.Core.Diff;
using GitForge.Core.Models;

namespace GitForge.Git.Tests;

/// <summary>Hunk and line staging for files git does not track yet, and for newly added files.</summary>
public class NewFileLineStagingTests
{
    [Fact]
    public async Task StageLines_FromUntrackedFile_AddsOnlyThoseLines()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("base.txt", "base\n");
        await t.CommitAllAsync("base");
        t.Write("new.txt", "one\ntwo\nthree\n");

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single(f => f.Path == "new.txt");
        Assert.Equal(FileChangeKind.Untracked, file.Kind);
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;
        Assert.True(diff.SupportsPartialStaging);

        await repo.Index.ApplySelectionAsync(diff, new HashSet<LineRef> { new(0, 0), new(0, 2) }, PatchAction.Stage);

        Assert.Equal("one\nthree\n", await t.GitAsync("show", ":new.txt"));
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(Path.Combine(t.Path, "new.txt")));
        var status = await repo.Read.GetStatusAsync();
        Assert.Contains(status.Staged, f => f is { Path: "new.txt", Kind: FileChangeKind.Added });
        Assert.Contains(status.Unstaged, f => f is { Path: "new.txt", Kind: FileChangeKind.Modified });
    }

    [Fact]
    public async Task StageLines_FromUntrackedCrlfFile_WithoutCommits_StoresNormalizedText()
    {
        await using var t = await TempRepo.CreateAsync();
        await t.GitAsync("config", "core.autocrlf", "true");
        t.Write("win.txt", "one\r\ntwo\r\nthree\r\n");

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;

        await repo.Index.ApplySelectionAsync(diff, new HashSet<LineRef> { new(0, 1) }, PatchAction.Stage);

        // Same bytes "git add" would store: line endings normalized to LF in the index, the file itself untouched.
        Assert.Equal("two\n", await t.GitAsync("show", ":win.txt"));
        Assert.Equal("one\r\ntwo\r\nthree\r\n", File.ReadAllText(Path.Combine(t.Path, "win.txt")));
    }

    [Fact]
    public async Task UnstageLines_FromAddedFile_KeepsTheRest_AndUnstagingEverythingRemovesTheEntry()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("base.txt", "base\n");
        await t.CommitAllAsync("base");
        t.Write("add.txt", "a\nb\nc\n");
        await t.GitAsync("add", "add.txt");

        var repo = await t.OpenAsync();
        var staged = (await repo.Read.GetStatusAsync()).Staged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(staged, new DiffOptions()))!;
        Assert.True(diff.IsNewFile);

        await repo.Index.ApplySelectionAsync(diff, new HashSet<LineRef> { new(0, 1) }, PatchAction.Unstage);
        Assert.Equal("a\nc\n", await t.GitAsync("show", ":add.txt"));

        staged = (await repo.Read.GetStatusAsync()).Staged.Single();
        diff = (await repo.Read.GetWorkingDiffAsync(staged, new DiffOptions()))!;
        await repo.Index.ApplySelectionAsync(diff, PatchBuilder.SelectHunk(diff.Hunks[0]), PatchAction.Unstage);

        var status = await repo.Read.GetStatusAsync();
        Assert.Empty(status.Staged);
        Assert.Contains(status.Unstaged, f => f is { Path: "add.txt", Kind: FileChangeKind.Untracked });
        Assert.Equal("a\nb\nc\n", File.ReadAllText(Path.Combine(t.Path, "add.txt")));
    }

    [Fact]
    public async Task DiscardLines_FromUntrackedFile_EditsOnlyTheWorkingTree()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("base.txt", "base\n");
        await t.CommitAllAsync("base");
        t.Write("new.txt", "keep\ndrop\nkeep too\n");

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single(f => f.Path == "new.txt");
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;

        await repo.Index.ApplySelectionAsync(diff, new HashSet<LineRef> { new(0, 1) }, PatchAction.Discard);

        Assert.Equal("keep\nkeep too\n", File.ReadAllText(Path.Combine(t.Path, "new.txt")));
        var status = await repo.Read.GetStatusAsync();
        Assert.Empty(status.Staged);
        Assert.Contains(status.Unstaged, f => f is { Path: "new.txt", Kind: FileChangeKind.Untracked });
    }
}
