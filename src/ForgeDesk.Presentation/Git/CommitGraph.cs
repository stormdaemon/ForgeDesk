using ForgeDesk.Core.Git;

namespace ForgeDesk.Presentation.Git;

/// <summary>Which half of a history row an edge is drawn in.</summary>
public enum GraphEdgePart
{
    /// <summary>From the top of the row (lane <c>From</c>) to its middle (lane <c>To</c>).</summary>
    Upper,

    /// <summary>From the middle of the row (lane <c>From</c>) to its bottom (lane <c>To</c>).</summary>
    Lower,
}

/// <summary>A line segment of the graph inside one row. Lanes are column indexes; Color indexes a palette.</summary>
public readonly record struct GraphEdge(int From, int To, int Color, GraphEdgePart Part);

/// <summary>
/// The graph cell of one commit row: its node (lane and color) and every line crossing the row.
/// <see cref="LaneCount"/> is the number of columns the row needs.
/// </summary>
public sealed record CommitGraphRow(int Lane, int Color, IReadOnlyList<GraphEdge> Edges, int LaneCount, bool IsMerge, bool HasChildren)
{
    public static readonly CommitGraphRow Empty = new(0, 0, [], 0, false, false);

    public bool Equals(CommitGraphRow? other) =>
        other is not null && Lane == other.Lane && Color == other.Color && LaneCount == other.LaneCount && IsMerge == other.IsMerge
        && HasChildren == other.HasChildren && Edges.SequenceEqual(other.Edges);

    public override int GetHashCode() => HashCode.Combine(Lane, Color, LaneCount, IsMerge, HasChildren, Edges.Count);
}

/// <summary>
/// Computes a commit graph over a list of commits in log order (children before parents), one row at
/// a time, so pages loaded later simply continue it. Each lane waits for the next commit of a line of
/// history; lanes stay in their column until that line ends, and freed columns are reused. Lines
/// whose commits are not loaded (yet) simply continue to the bottom.
/// </summary>
public sealed class CommitGraphBuilder
{
    /// <summary>Number of distinct lane colors; <see cref="GraphEdge.Color"/> is below this.</summary>
    public const int PaletteSize = 8;

    private readonly List<string?> _lanes = [];
    private readonly List<int> _colors = [];
    private int _nextColor;

    /// <summary>Widest row computed so far (in lanes).</summary>
    public int MaxLaneCount { get; private set; }

    public static IReadOnlyList<CommitGraphRow> Compute(IEnumerable<GitCommit> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var builder = new CommitGraphBuilder();
        return commits.Select(builder.Add).ToList();
    }

    public CommitGraphRow Add(GitCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return Add(commit.Sha, commit.Parents);
    }

    public CommitGraphRow Add(string sha, IReadOnlyList<string> parents)
    {
        ArgumentNullException.ThrowIfNull(sha);
        ArgumentNullException.ThrowIfNull(parents);
        var edges = new List<GraphEdge>();

        // 1. The lanes waiting for this commit converge into its node (the leftmost of them).
        var node = -1;
        var incoming = new List<int>();
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (string.Equals(_lanes[i], sha, StringComparison.Ordinal))
            {
                incoming.Add(i);
                if (node < 0)
                {
                    node = i;
                }
            }
        }

        var hasChildren = node >= 0;
        if (!hasChildren)
        {
            // A branch tip: a new line starts here.
            node = Allocate(sha);
        }

        var nodeColor = _colors[node];

        // 2. Upper half: lines coming from the row above.
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i] is not { } waiting)
            {
                continue;
            }

            if (string.Equals(waiting, sha, StringComparison.Ordinal))
            {
                if (hasChildren)
                {
                    edges.Add(new GraphEdge(i, node, _colors[i], GraphEdgePart.Upper));
                }
            }
            else
            {
                edges.Add(new GraphEdge(i, i, _colors[i], GraphEdgePart.Upper));
            }
        }

        foreach (var lane in incoming.Where(lane => lane != node))
        {
            _lanes[lane] = null;
        }

        // 3. Parents: the first continues the node's lane, the others join or open lanes.
        var forkedLanes = new HashSet<int>();
        if (parents.Count == 0)
        {
            _lanes[node] = null;
        }
        else
        {
            _lanes[node] = parents[0];
            for (var p = 1; p < parents.Count; p++)
            {
                var parent = parents[p];
                var existing = IndexOf(parent);
                if (existing >= 0 && existing != node)
                {
                    edges.Add(new GraphEdge(node, existing, _colors[existing], GraphEdgePart.Lower));
                }
                else if (existing < 0)
                {
                    var lane = Allocate(parent);
                    forkedLanes.Add(lane);
                    edges.Add(new GraphEdge(node, lane, _colors[lane], GraphEdgePart.Lower));
                }
            }
        }

        // 4. Lower half: lines continuing to the row below.
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i] is null || forkedLanes.Contains(i))
            {
                continue;
            }

            edges.Add(new GraphEdge(i, i, _colors[i], GraphEdgePart.Lower));
        }

        var laneCount = _lanes.Count;
        while (_lanes.Count > 0 && _lanes[^1] is null)
        {
            _lanes.RemoveAt(_lanes.Count - 1);
            _colors.RemoveAt(_colors.Count - 1);
        }

        MaxLaneCount = Math.Max(MaxLaneCount, laneCount);
        return new CommitGraphRow(node, nodeColor, edges, laneCount, parents.Count > 1, hasChildren);
    }

    private int IndexOf(string sha)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (string.Equals(_lanes[i], sha, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private int Allocate(string sha)
    {
        var color = _nextColor++ % PaletteSize;
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i] is null)
            {
                _lanes[i] = sha;
                _colors[i] = color;
                return i;
            }
        }

        _lanes.Add(sha);
        _colors.Add(color);
        return _lanes.Count - 1;
    }
}
