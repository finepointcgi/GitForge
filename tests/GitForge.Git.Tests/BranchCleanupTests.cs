using GitForge.Core.Models;

namespace GitForge.Git.Tests;

/// <summary>Deciding which stale branches are safe to delete, and deleting them without losing work.</summary>
public class BranchCleanupTests
{
    [Fact]
    public async Task CheckMerged_RecognizesMergedSquashedAndRebasedBranches()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "a\n");
        await t.CommitAllAsync("init");

        async Task BranchAsync(string name, params (string File, string Text)[] commits)
        {
            await t.GitAsync("checkout", "-q", "-b", name, "main");
            foreach (var (file, text) in commits)
            {
                t.Write(file, text);
                await t.CommitAllAsync($"{name}: {file}");
            }

            await t.GitAsync("checkout", "-q", "main");
        }

        await BranchAsync("merged", ("m.txt", "m\n"));
        await t.GitAsync("merge", "-q", "--no-ff", "-m", "merge merged", "merged");

        await BranchAsync("squashed", ("s.txt", "s1\n"), ("s.txt", "s2\n"), ("s2.txt", "x\n"));
        await t.GitAsync("merge", "-q", "--squash", "squashed");
        await t.GitAsync("commit", "-q", "-m", "squashed (#1)");

        await BranchAsync("rebased", ("r.txt", "r1\n"), ("r2.txt", "r2\n"));
        t.Write("a.txt", "a changed on main\n");
        await t.CommitAllAsync("main moves on");
        await t.GitAsync("cherry-pick", "main..rebased");

        await BranchAsync("more-after-squash", ("p.txt", "p\n"));
        await t.GitAsync("merge", "-q", "--squash", "more-after-squash");
        await t.GitAsync("commit", "-q", "-m", "more-after-squash (#2)");
        await t.GitAsync("checkout", "-q", "more-after-squash");
        t.Write("p.txt", "p, more\n");
        await t.CommitAllAsync("work after the merge");
        await t.GitAsync("checkout", "-q", "main");

        await BranchAsync("unmerged", ("u.txt", "u1\n"), ("u.txt", "u2\n"));

        // Merged into the checked-out branch but not into main: deleting it loses nothing either.
        await t.GitAsync("checkout", "-q", "-b", "feature/current", "main");
        await BranchAsync("into-head", ("h.txt", "h\n"));
        await t.GitAsync("checkout", "-q", "feature/current");
        await t.GitAsync("merge", "-q", "--ff-only", "into-head");

        // Commit signing must not be attempted (it would fail here, or prompt for a passphrase on a real machine).
        await t.GitAsync("config", "commit.gpgsign", "true");
        await t.GitAsync("config", "gpg.program", "gitforge-no-such-gpg");
        var refsBefore = await t.GitAsync("for-each-ref");

        var repo = await t.OpenAsync();
        var checks = await repo.Branches.CheckMergedAsync(["merged", "squashed", "rebased", "more-after-squash", "unmerged", "into-head", "no-such-branch"], "main");

        Assert.Equal(["merged", "squashed", "rebased", "more-after-squash", "unmerged", "into-head"], checks.Select(c => c.Branch));
        var state = checks.ToDictionary(c => c.Branch);
        Assert.Equal(BranchMergeState.Merged, state["merged"].State);
        Assert.Equal(BranchMergeState.ChangesInBase, state["squashed"].State);
        Assert.Equal(BranchMergeState.ChangesInBase, state["rebased"].State);
        Assert.Equal(BranchMergeState.Unmerged, state["more-after-squash"].State);
        Assert.Equal(1, state["more-after-squash"].UnmergedCommits);
        Assert.Equal(BranchMergeState.Unmerged, state["unmerged"].State);
        Assert.Equal(2, state["unmerged"].UnmergedCommits);
        Assert.Equal(BranchMergeState.Merged, state["into-head"].State);
        Assert.Equal((await t.GitAsync("rev-parse", "squashed")).Trim(), state["squashed"].TipSha);

        // Checking changes nothing.
        Assert.Equal(refsBefore, await t.GitAsync("for-each-ref"));
        Assert.Equal(string.Empty, (await t.GitAsync("status", "--porcelain")).Trim());

        // Against a base without the merges, only the branch merged into HEAD stays merged.
        var root = (await t.GitAsync("rev-list", "--max-parents=0", "HEAD")).Trim();
        await t.GitAsync("branch", "old-main", root);
        var old = (await repo.Branches.CheckMergedAsync(["squashed", "into-head"], "old-main")).ToDictionary(c => c.Branch);
        Assert.Equal(BranchMergeState.Unmerged, old["squashed"].State);
        Assert.Equal(4, old["squashed"].UnmergedCommits); // its three commits and the one from "merged", which it was started after
        Assert.Equal(BranchMergeState.Merged, old["into-head"].State);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Branches.CheckMergedAsync(["merged"], "origin/no-such-branch"));
    }

    [Fact]
    public async Task DeleteIfUnchanged_KeepsABranchThatMovedSinceItWasChecked()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "a\n");
        await t.CommitAllAsync("init");
        await t.GitAsync("branch", "topic");
        var checkedTip = (await t.GitAsync("rev-parse", "topic")).Trim();
        await t.GitAsync("checkout", "-q", "topic");
        t.Write("a.txt", "new work\n");
        await t.CommitAllAsync("new work");
        await t.GitAsync("checkout", "-q", "main");
        var repo = await t.OpenAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Branches.DeleteIfUnchangedAsync("topic", checkedTip));
        Assert.Contains("new commits", ex.Message, StringComparison.Ordinal);
        Assert.Contains("topic", await t.GitAsync("branch", "--list", "topic"));

        await repo.Branches.DeleteIfUnchangedAsync("topic", (await t.GitAsync("rev-parse", "topic")).Trim());
        Assert.Equal(string.Empty, (await t.GitAsync("branch", "--list", "topic")).Trim());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Branches.DeleteIfUnchangedAsync("topic", checkedTip));
    }

    [Fact]
    public async Task Worktree_HasChangesSeesEditsAndUntrackedFiles()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "a\n");
        await t.CommitAllAsync("init");
        var repo = await t.OpenAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"gitforge-wt-{Guid.NewGuid():N}");
        try
        {
            await repo.Worktrees.AddAsync(folder, "main", "topic");
            Assert.False(await repo.Worktrees.HasChangesAsync(folder));

            await File.WriteAllTextAsync(Path.Combine(folder, "new.txt"), "untracked\n");
            Assert.True(await repo.Worktrees.HasChangesAsync(folder));
            File.Delete(Path.Combine(folder, "new.txt"));

            await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "edited\n");
            Assert.True(await repo.Worktrees.HasChangesAsync(folder));
            Assert.False(await repo.Worktrees.HasChangesAsync(t.Path));
        }
        finally
        {
            if (Directory.Exists(folder)) await repo.Worktrees.RemoveAsync(folder, force: true);
        }
    }
}
