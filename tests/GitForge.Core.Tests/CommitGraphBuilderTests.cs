using GitForge.Core.Graph;
using GitForge.Core.Models;

namespace GitForge.Core.Tests;

public class CommitGraphBuilderTests
{
    private static CommitInfo C(string sha, params string[] parents) =>
        new(sha, parents, "a", "a@example.com", DateTimeOffset.UnixEpoch, sha);

    private static List<GraphRow> Layout(params CommitInfo[] commits)
    {
        var b = new CommitGraphBuilder();
        return commits.Select(b.Add).ToList();
    }

    [Fact]
    public void LinearHistory_StaysOnLaneZero()
    {
        var rows = Layout(C("c3", "c2"), C("c2", "c1"), C("c1"));

        Assert.All(rows, r => Assert.Equal(0, r.NodeLane));
        Assert.All(rows, r => Assert.Equal(1, r.LaneCount));
        Assert.DoesNotContain(rows[0].Edges, e => e.Kind == EdgeKind.IntoNode); // branch tip: nothing above
        Assert.DoesNotContain(rows[2].Edges, e => e.Kind == EdgeKind.OutOfNode); // root: nothing below
        Assert.Single(rows[1].Edges, e => e.Kind == EdgeKind.IntoNode && e.FromLane == 0);
    }

    [Fact]
    public void MergeCommit_OpensSecondLane_AndForkJoinsBack()
    {
        // m merges f into main; f and b both descend from base.
        var rows = Layout(
            C("m", "b", "f"),
            C("f", "base"),
            C("b", "base"),
            C("base"));

        var merge = rows[0];
        Assert.Equal(0, merge.NodeLane);
        Assert.Contains(merge.Edges, e => e is { Kind: EdgeKind.OutOfNode, FromLane: 0, ToLane: 0 });
        Assert.Contains(merge.Edges, e => e is { Kind: EdgeKind.OutOfNode, FromLane: 0, ToLane: 1 });
        Assert.Equal(2, merge.LaneCount);

        var feature = rows[1];
        Assert.Equal(1, feature.NodeLane);
        Assert.Contains(feature.Edges, e => e is { Kind: EdgeKind.PassThrough, FromLane: 0 });

        // b's parent "base" is also expected by lane 1 (to the right), so both lanes converge at base.
        var root = rows[3];
        Assert.Equal(0, root.NodeLane);
        Assert.Contains(root.Edges, e => e is { Kind: EdgeKind.IntoNode, FromLane: 1, ToLane: 0 });
        Assert.Equal(2, root.LaneCount);
    }

    [Fact]
    public void BranchPoint_JoinsLeftLane_AndFreesItsOwn()
    {
        // Two tips: main (lane 0) and topic (lane 1). topic's parent is main's parent.
        var rows = Layout(C("main2", "main1"), C("topic", "main1"), C("main1"));

        var topic = rows[1];
        Assert.Equal(1, topic.NodeLane);
        Assert.Contains(topic.Edges, e => e is { Kind: EdgeKind.OutOfNode, FromLane: 1, ToLane: 0 });

        var b = new CommitGraphBuilder();
        foreach (var c in new[] { C("main2", "main1"), C("topic", "main1") }) b.Add(c);
        Assert.Equal(1, b.ActiveLaneCount); // lane 1 freed and trimmed
    }

    [Fact]
    public void FreedLanes_AreReused()
    {
        var rows = Layout(
            C("a", "root"),
            C("b", "root"), // joins lane 0, frees lane 1
            C("c", "root")); // new tip should reuse lane 1, not open lane 2

        Assert.Equal(1, rows[2].NodeLane);
        Assert.All(rows, r => Assert.True(r.LaneCount <= 2));
    }

    [Fact]
    public void FirstParent_KeepsBranchColor()
    {
        var rows = Layout(C("c2", "c1"), C("c1", "c0"), C("c0"));
        Assert.All(rows, r => Assert.Equal(rows[0].NodeColorIndex, r.NodeColorIndex));
    }

    [Fact]
    public void LargeHistory_IsLinearTime()
    {
        var b = new CommitGraphBuilder();
        const int n = 200_000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = n; i > 0; i--)
        {
            var parents = i % 50 == 0 && i > 2 ? new[] { $"c{i - 1}", $"c{i - 2}" } : new[] { $"c{i - 1}" };
            b.Add(C($"c{i}", i == 1 ? [] : parents));
        }

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Layout of {n:N0} commits took {sw.Elapsed}.");
    }

    [Fact]
    public void Geometry_ProducesStraightAndCurvedPaths()
    {
        var g = new GraphGeometry(LaneWidth: 10, RowHeight: 20);
        Assert.Equal("M5,0 L5,20", g.PathFor(new GraphEdge(EdgeKind.PassThrough, 0, 0, 0), 0));
        Assert.StartsWith("M5,10 C", g.PathFor(new GraphEdge(EdgeKind.OutOfNode, 0, 1, 0), 0), StringComparison.Ordinal);
        Assert.Equal(30, g.Width(3));
    }
}
