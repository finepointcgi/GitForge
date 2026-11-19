using GitForge.Core.Models;
using GitForge.UI.Services;

namespace GitForge.UI.Tests;

public class BranchCleanupTests
{
    private static BranchHead Local(string name, bool current = false) =>
        new(name, $"refs/heads/{name}", "a1", IsRemote: false, IsCurrent: current, RemoteName: null, UpstreamName: null, Ahead: null, Behind: null);

    private static BranchHead Remote(string remote, string name) =>
        new($"{remote}/{name}", $"refs/remotes/{remote}/{name}", "b2", IsRemote: true, IsCurrent: false, RemoteName: remote, UpstreamName: null, Ahead: null, Behind: null);

    private static RepositorySnapshot Snapshot(BranchHead[] locals, BranchHead[] remotes) =>
        new("C:\\src\\app", "app", null, null, false, false, RepositoryOperation.None, locals, remotes, [], [], [], []);

    [Fact]
    public void BaseCandidates_PutTheRemoteDefaultBranchFirstAndTheCurrentBranchLast()
    {
        var snapshot = Snapshot(
            [Local("main"), Local("develop"), Local("feature/ddil", current: true)],
            [Remote("upstream", "main"), Remote("origin", "main"), Remote("origin", "develop"), Remote("origin", "feature/main")]);

        Assert.Equal(["origin/develop", "origin/main", "upstream/main", "main", "develop", "feature/ddil"],
            BranchCleanup.BaseCandidates(snapshot, "origin/develop", "feature/ddil"));

        // No remote default (or one that no longer exists): trunk names in order, never a branch that merely ends in "main".
        Assert.Equal(["origin/main", "upstream/main", "main", "origin/develop", "develop", "feature/ddil"],
            BranchCleanup.BaseCandidates(snapshot, "origin/gone", "feature/ddil"));
    }

    [Fact]
    public void BaseCandidates_FallBackToTheCurrentBranch()
    {
        var snapshot = Snapshot([Local("work", current: true)], []);
        Assert.Equal(["work"], BranchCleanup.BaseCandidates(snapshot, null, "work"));
        Assert.Empty(BranchCleanup.BaseCandidates(snapshot, null, null));
    }

    [Theory]
    [InlineData("refs/remotes/origin/main\n", "origin/main")]
    [InlineData("refs/remotes/upstream/release/2.0", "upstream/release/2.0")]
    [InlineData("refs/remotes/", null)]
    [InlineData("", null)]
    public void RemoteDefault_ReadsSymbolicRefOutput(string output, string? expected) =>
        Assert.Equal(expected, BranchCleanup.RemoteDefault(output));
}
