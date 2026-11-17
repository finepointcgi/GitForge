using GitForge.Core.Diff;
using GitForge.Core.Graph;
using GitForge.Core.Models;
using GitForge.Core.Rebase;

namespace GitForge.Git.Tests;

/// <summary>Integration tests against real git repositories. Requires git 2.39+ on PATH.</summary>
public class GitRepositoryTests
{
    private static string Lines(int from, int to, Func<int, string>? f = null) =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => (f?.Invoke(i) ?? $"line {i}") + "\n"));

    [Fact]
    public async Task Snapshot_Status_And_History()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "one\n");
        await t.CommitAllAsync("first");
        await t.GitAsync("checkout", "-q", "-b", "topic");
        t.Write("b.txt", "two\n");
        await t.CommitAllAsync("second");
        await t.GitAsync("checkout", "-q", "main");
        await t.GitAsync("merge", "-q", "--no-ff", "-m", "merge topic", "topic");
        await t.GitAsync("tag", "v1");
        t.Write("a.txt", "one changed\n");
        t.Write("new.txt", "untracked\n");

        var repo = await t.OpenAsync();
        var snap = await repo.Read.GetSnapshotAsync();
        Assert.Equal("main", snap.HeadBranch);
        Assert.Equal(["main", "topic"], snap.LocalBranches.Select(b => b.Name));
        Assert.Contains(snap.Tags, tag => tag.Name == "v1" && tag.TargetSha == snap.HeadSha);

        var status = await repo.Read.GetStatusAsync();
        Assert.Contains(status.Unstaged, f => f is { Path: "a.txt", Kind: FileChangeKind.Modified });
        Assert.Contains(status.Unstaged, f => f is { Path: "new.txt", Kind: FileChangeKind.Untracked });
        Assert.Empty(status.Staged);

        var builder = new CommitGraphBuilder();
        var rows = new List<GraphRow>();
        await foreach (var c in repo.Read.GetHistoryAsync(new HistoryQuery())) rows.Add(builder.Add(c));
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].Commit.IsMerge);
        Assert.Equal(2, rows.Max(r => r.LaneCount));

        var details = await repo.Read.GetCommitDetailsAsync(rows[^1].Commit.Sha);
        Assert.Equal("first", details.Summary.Subject);
        Assert.Single(details.Changes);
    }

    [Fact]
    public async Task StageOneHunk_LeavesOtherHunkUnstaged()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("f.txt", Lines(1, 30));
        await t.CommitAllAsync("base");
        t.Write("f.txt", Lines(1, 30, i => i switch { 2 => "line 2 EDIT", 28 => "line 28 EDIT", _ => $"line {i}" }));

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;
        Assert.Equal(2, diff.Hunks.Count);

        await repo.Index.ApplySelectionAsync(diff, PatchBuilder.SelectHunk(diff.Hunks[1]), PatchAction.Stage);

        var cached = await t.GitAsync("diff", "--cached");
        Assert.Contains("+line 28 EDIT", cached, StringComparison.Ordinal);
        Assert.DoesNotContain("line 2 EDIT", cached, StringComparison.Ordinal);
        var unstaged = await t.GitAsync("diff");
        Assert.Contains("+line 2 EDIT", unstaged, StringComparison.Ordinal);
        Assert.DoesNotContain("line 28 EDIT", unstaged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageSingleLine_ThenUnstageIt()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("f.txt", Lines(1, 5));
        await t.CommitAllAsync("base");
        t.Write("f.txt", "line 1\nline 2\nNEW A\nNEW B\nline 3\nline 4\nline 5\n");

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;
        var hunk = diff.Hunks.Single();
        var newB = hunk.Lines.ToList().FindIndex(l => l.Text == "NEW B");

        await repo.Index.ApplySelectionAsync(diff, new HashSet<LineRef> { new(0, newB) }, PatchAction.Stage);
        Assert.Equal("line 1\nline 2\nNEW B\nline 3\nline 4\nline 5\n", await t.GitAsync("show", ":f.txt"));

        var staged = (await repo.Read.GetStatusAsync()).Staged.Single();
        var stagedDiff = (await repo.Read.GetWorkingDiffAsync(staged, new DiffOptions()))!;
        await repo.Index.ApplySelectionAsync(stagedDiff, PatchBuilder.SelectHunk(stagedDiff.Hunks[0]), PatchAction.Unstage);
        Assert.Equal(Lines(1, 5), await t.GitAsync("show", ":f.txt"));
    }

    [Fact]
    public async Task DiscardHunk_RevertsOnlySelection_WithCrlfContent()
    {
        await using var t = await TempRepo.CreateAsync();
        static string Crlf(int edit1, int edit2) => string.Concat(Enumerable.Range(1, 20).Select(i => i == edit1 || i == edit2 ? $"EDIT {i}\r\n" : $"line {i}\r\n"));
        t.Write("w.txt", Crlf(-1, -1));
        await t.CommitAllAsync("base");
        t.Write("w.txt", Crlf(2, 18));

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions()))!;
        Assert.Equal(2, diff.Hunks.Count);
        Assert.True(diff.Hunks[0].Lines[0].HasCarriageReturn);

        await repo.Index.ApplySelectionAsync(diff, PatchBuilder.SelectHunk(diff.Hunks[0]), PatchAction.Discard);
        Assert.Equal(Crlf(18, -1), File.ReadAllText(Path.Combine(t.Path, "w.txt")));
    }

    [Fact]
    public async Task DiscardLines_WithZeroContextDiff_UsesUnidiffZero()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("z.txt", "a\nb\nc\nd\n");
        await t.CommitAllAsync("base");
        t.Write("z.txt", "a\nB1\nc\nD1\n");

        var repo = await t.OpenAsync();
        var file = (await repo.Read.GetStatusAsync()).Unstaged.Single();
        var diff = (await repo.Read.GetWorkingDiffAsync(file, new DiffOptions(ContextLines: 0)))!;
        Assert.Equal(2, diff.Hunks.Count);

        await repo.Index.ApplySelectionAsync(diff, PatchBuilder.SelectHunk(diff.Hunks[0]), PatchAction.Discard);
        Assert.Equal("a\nb\nc\nD1\n", File.ReadAllText(Path.Combine(t.Path, "z.txt")));
    }

    [Fact]
    public async Task Commit_Branch_Stash_RoundTrip()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "1\n");
        await t.CommitAllAsync("init");
        var repo = await t.OpenAsync();

        await repo.Branches.CreateAsync("feature/x", "HEAD", checkout: true);
        t.Write("a.txt", "2\n");
        await repo.Index.StageAsync(["a.txt"]);
        var sha = await repo.Index.CommitAsync(new CommitRequest("change a\n\nbody text"));
        Assert.Equal(40, sha.Length);
        Assert.Equal("feature/x", (await repo.Read.GetSnapshotAsync()).HeadBranch);

        t.Write("a.txt", "3\n");
        await repo.Stashes.SaveAsync(new StashRequest("wip"));
        Assert.True((await repo.Read.GetStatusAsync()).IsClean);
        Assert.Single((await repo.Read.GetSnapshotAsync()).Stashes);
        await repo.Stashes.ApplyAsync(0, pop: true, restoreIndex: false);
        Assert.Equal("3\n", File.ReadAllText(Path.Combine(t.Path, "a.txt")));

        await Assert.ThrowsAsync<ArgumentException>(() => repo.Branches.CreateAsync("--force", "HEAD", false));
    }

    [Fact]
    public async Task InteractiveRebase_ReordersSquashesAndRewords()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("base.txt", "b\n");
        await t.CommitAllAsync("base");
        var baseSha = (await t.GitAsync("rev-parse", "HEAD")).Trim();
        foreach (var n in new[] { "one", "two", "three" })
        {
            t.Write($"{n}.txt", n + "\n");
            await t.CommitAllAsync(n);
        }

        var repo = await t.OpenAsync();
        var plan = (await repo.History.GetRebasePlanAsync(baseSha)).ToList();
        Assert.Equal(["one", "two", "three"], plan.Select(p => p.Subject));

        // New order: three (reworded), one, two squashed into one.
        var edited = new List<RebaseTodoItem>
        {
            plan[2] with { Action = RebaseAction.Reword, NewMessage = "THREE reworded" },
            plan[0],
            plan[1] with { Action = RebaseAction.Fixup },
        };
        var result = await repo.History.RunInteractiveRebaseAsync(baseSha, edited);
        Assert.True(result.Succeeded, result.StandardError);

        var log = (await t.GitAsync("log", "--format=%s", $"{baseSha}..HEAD")).Trim().Split('\n');
        Assert.Equal(["one", "THREE reworded"], log);
        Assert.True(File.Exists(Path.Combine(t.Path, "two.txt")));
    }

    [Fact]
    public async Task Watcher_RaisesRefsChange_OnCommit()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "1\n");
        await t.CommitAllAsync("init");
        var repo = await t.OpenAsync();
        var tcs = new TaskCompletionSource<RepositoryChangeKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.Changed += (_, e) =>
        {
            if (e.Kind.HasFlag(RepositoryChangeKind.Refs)) tcs.TrySetResult(e.Kind);
        };

        t.Write("a.txt", "2\n");
        await t.CommitAllAsync("second");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(tcs.Task, completed);
    }
}
