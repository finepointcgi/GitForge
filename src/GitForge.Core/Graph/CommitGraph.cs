using GitForge.Core.Models;

namespace GitForge.Core.Graph;

public enum EdgeKind
{
    /// <summary>Lane continues straight through the row (top → bottom).</summary>
    PassThrough,

    /// <summary>Lane enters the row from the top and ends at the commit node (child → this commit).</summary>
    IntoNode,

    /// <summary>Edge leaves the commit node and exits the bottom of the row (this commit → parent).</summary>
    OutOfNode,
}

/// <summary>A single drawable segment within one graph row. Lanes are zero-based columns.</summary>
public readonly record struct GraphEdge(EdgeKind Kind, int FromLane, int ToLane, int ColorIndex);

/// <summary>Everything needed to draw one row of the commit graph without looking at neighbors.</summary>
public sealed record GraphRow(int RowIndex, CommitInfo Commit, int NodeLane, int NodeColorIndex, IReadOnlyList<GraphEdge> Edges, int LaneCount);

/// <summary>
/// Incremental, streaming lane-assignment layout (O(rows × active lanes)).
/// Commits must arrive in topological order (children before parents), e.g. <c>git log --topo-order</c>.
/// </summary>
/// <remarks>
/// Each lane holds the SHA it expects next. A commit lands on the first lane expecting it; any other lanes
/// expecting it converge into the node. The first parent inherits the node's lane and color (stable branch color);
/// additional merge parents reuse an existing lane if one already expects them, otherwise they claim the
/// left-most free lane. Free lanes in the middle are reused; trailing free lanes are trimmed.
/// </remarks>
public sealed class CommitGraphBuilder
{
    private readonly List<string?> _lanes = [];
    private readonly List<int> _laneColors = [];
    private readonly int _paletteSize;
    private int _nextColor;
    private int _rowIndex;

    public CommitGraphBuilder(int paletteSize = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(paletteSize, 1);
        _paletteSize = paletteSize;
    }

    /// <summary>Number of lanes currently active (useful for sizing the graph column).</summary>
    public int ActiveLaneCount => _lanes.Count;

    public GraphRow Add(CommitInfo commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var edges = new List<GraphEdge>(_lanes.Count + commit.ParentShas.Count);
        var topLaneCount = _lanes.Count;

        // 1. Locate the node lane (first lane expecting this commit) or open a new branch tip lane.
        var nodeLane = _lanes.IndexOf(commit.Sha);
        if (nodeLane < 0)
        {
            nodeLane = AllocateLane();
            _laneColors[nodeLane] = NextColor();
        }

        var nodeColor = _laneColors[nodeLane];

        // 2. Top half: converge every lane that expected this commit; everything else passes through.
        for (var i = 0; i < topLaneCount; i++)
        {
            var expected = _lanes[i];
            if (expected is null) continue;
            if (expected == commit.Sha)
            {
                edges.Add(new GraphEdge(EdgeKind.IntoNode, i, nodeLane, _laneColors[i]));
                if (i != nodeLane) _lanes[i] = null;
            }
            else
            {
                edges.Add(new GraphEdge(EdgeKind.PassThrough, i, i, _laneColors[i]));
            }
        }

        // 3. Bottom half: route parents.
        if (commit.ParentShas.Count == 0)
        {
            _lanes[nodeLane] = null;
        }
        else
        {
            var first = commit.ParentShas[0];
            var existing = FindLane(first, exclude: nodeLane);
            if (existing >= 0 && existing < nodeLane)
            {
                // Branch point: the first parent is already expected on a lane to the left. Join it and free ours,
                // so long-lived lines stay left-most. (A lane to the right instead converges at the parent.)
                edges.Add(new GraphEdge(EdgeKind.OutOfNode, nodeLane, existing, _laneColors[existing]));
                _lanes[nodeLane] = null;
            }
            else
            {
                _lanes[nodeLane] = first;
                edges.Add(new GraphEdge(EdgeKind.OutOfNode, nodeLane, nodeLane, nodeColor));
            }

            for (var p = 1; p < commit.ParentShas.Count; p++)
            {
                var parent = commit.ParentShas[p];
                var lane = FindLane(parent, exclude: -1);
                if (lane < 0)
                {
                    lane = AllocateLane();
                    _lanes[lane] = parent;
                    _laneColors[lane] = NextColor();
                }

                edges.Add(new GraphEdge(EdgeKind.OutOfNode, nodeLane, lane, _laneColors[lane]));
            }
        }

        var laneCount = Math.Max(topLaneCount, _lanes.Count);
        laneCount = Math.Max(laneCount, nodeLane + 1);
        TrimTrailingFreeLanes();

        return new GraphRow(_rowIndex++, commit, nodeLane, nodeColor, edges, laneCount);
    }

    private int FindLane(string sha, int exclude)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (i != exclude && _lanes[i] == sha) return i;
        }

        return -1;
    }

    private int AllocateLane()
    {
        var free = _lanes.IndexOf(null);
        if (free >= 0) return free;
        _lanes.Add(null);
        _laneColors.Add(0);
        return _lanes.Count - 1;
    }

    private int NextColor()
    {
        var c = _nextColor;
        _nextColor = (_nextColor + 1) % _paletteSize;
        return c;
    }

    private void TrimTrailingFreeLanes()
    {
        while (_lanes.Count > 0 && _lanes[^1] is null)
        {
            _lanes.RemoveAt(_lanes.Count - 1);
            _laneColors.RemoveAt(_laneColors.Count - 1);
        }
    }
}

/// <summary>Pixel geometry for rendering rows as SVG paths. Pure math, shared by every renderer.</summary>
public sealed record GraphGeometry(double LaneWidth = 14, double RowHeight = 26, double NodeRadius = 4)
{
    public double X(int lane) => (lane * LaneWidth) + (LaneWidth / 2);

    public double Width(int laneCount) => Math.Max(1, laneCount) * LaneWidth;

    /// <summary>SVG path data for one edge (straight when lanes match, cubic Bézier otherwise).</summary>
    public string PathFor(GraphEdge edge, int nodeLane)
    {
        var h = RowHeight;
        var mid = h / 2;
        return edge.Kind switch
        {
            EdgeKind.PassThrough => Line(X(edge.FromLane), 0, X(edge.ToLane), h),
            EdgeKind.IntoNode => Curve(X(edge.FromLane), 0, X(nodeLane), mid),
            EdgeKind.OutOfNode => Curve(X(nodeLane), mid, X(edge.ToLane), h),
            _ => string.Empty,
        };
    }

    private static string Line(double x1, double y1, double x2, double y2) =>
        FormattableString.Invariant($"M{x1:0.#},{y1:0.#} L{x2:0.#},{y2:0.#}");

    private static string Curve(double x1, double y1, double x2, double y2)
    {
        if (Math.Abs(x1 - x2) < 0.01) return Line(x1, y1, x2, y2);
        var dy = (y2 - y1) * 0.6;
        return FormattableString.Invariant($"M{x1:0.#},{y1:0.#} C{x1:0.#},{y1 + dy:0.#} {x2:0.#},{y2 - dy:0.#} {x2:0.#},{y2:0.#}");
    }
}
