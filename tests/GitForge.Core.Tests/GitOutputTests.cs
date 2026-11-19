using GitForge.Core.Models;

namespace GitForge.Core.Tests;

public class GitOutputTests
{
    [Fact]
    public void SubmodulesMissingOnRemote_ReadsGitsRefusal()
    {
        const string message = "The following submodule paths contain changes that can\nnot be found on any remote:\n  libs/sub\n  vendor/x y\n\nPlease try\n\n\tgit push --recurse-submodules=on-demand\n\nor cd to the path and use\n\n\tgit push\n\nto push them to a remote.\n\nfatal: Aborting.";
        Assert.Equal(["libs/sub", "vendor/x y"], GitOutput.SubmodulesMissingOnRemote(message));
        Assert.Empty(GitOutput.SubmodulesMissingOnRemote("! [rejected] main -> main (fetch first)"));
    }

    [Fact]
    public void ParseWorktrees_ReadsPorcelainWithAndWithoutNul()
    {
        var main = Path.Combine(Path.GetTempPath(), "repo");
        var other = Path.Combine(Path.GetTempPath(), "repo-feature");
        var text = $"worktree {main}\nHEAD {new string('a', 40)}\nbranch refs/heads/main\n\nworktree {other}\nHEAD {new string('b', 40)}\ndetached\nlocked on a USB drive\n\n";

        foreach (var porcelain in new[] { text, text.Replace('\n', '\0') })
        {
            var list = GitOutput.ParseWorktrees(porcelain, other);
            Assert.Equal(2, list.Count);
            Assert.True(list[0] is { IsMain: true, IsCurrent: false, Branch: "main", IsLocked: false });
            Assert.True(list[1] is { IsMain: false, IsCurrent: true, Branch: null, IsLocked: true, LockReason: "on a USB drive" });
            Assert.Equal("repo-feature", list[1].Name);
        }
    }

    [Theory]
    [InlineData("From 1234567890123456789012345678901234567890 Mon Sep 17 00:00:00 2001\nFrom: A <a@b.c>\nSubject: [PATCH] x\n", true)]
    [InlineData("diff --git a/x b/x\nindex 1..2 100644\n", false)]
    [InlineData("From the desk of someone\nno subject here", false)]
    public void IsMailbox_TellsFormatPatchFromPlainDiffs(string text, bool expected) => Assert.Equal(expected, GitOutput.IsMailbox(text));

    [Fact]
    public void ParseBisect_ReadsRefsAndTheFoundCommit()
    {
        var bad = new string('b', 40);
        var good = new string('a', 40);
        var refs = $"refs/bisect/bad {bad}\nrefs/bisect/good-{good} {good}\nrefs/bisect/skip-{new string('c', 40)} {new string('c', 40)}\n";

        var running = GitOutput.ParseBisect(refs, "git bisect start\n", 12);
        Assert.Equal((bad, 1, 1, (string?)null, 12), (running.Bad, running.Good.Count, running.Skipped, running.FirstBad, running.Remaining));
        Assert.Equal(4, running.StepsLeft);
        Assert.False(running.NeedsGood);

        var done = GitOutput.ParseBisect(refs, $"git bisect bad {bad}\n# first bad commit: [{bad}] broke it\n", 12);
        Assert.Equal(bad, done.FirstBad);
        Assert.Equal(0, done.Remaining);

        Assert.True(GitOutput.ParseBisect($"refs/bisect/bad {bad}\n", "git bisect start\n", null).NeedsGood);
        Assert.Equal(bad, GitOutput.FirstBadCommit($"{bad} is the first bad commit\ncommit {bad}\n"));
        Assert.Null(GitOutput.FirstBadCommit("Bisecting: 3 revisions left to test after this (roughly 2 steps)"));
    }
}
