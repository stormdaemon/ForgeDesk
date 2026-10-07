using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Tests.Git.Support;

namespace ForgeDesk.Presentation.Tests.Git;

public sealed class CommitGraphTests
{
    private static GraphEdge Up(int from, int to, int color) => new(from, to, color, GraphEdgePart.Upper);

    private static GraphEdge Down(int from, int to, int color) => new(from, to, color, GraphEdgePart.Lower);

    [Fact]
    public void Linear_history_stays_in_one_lane()
    {
        var rows = CommitGraphBuilder.Compute([GitData.Commit("c", "C", "b"), GitData.Commit("b", "B", "a"), GitData.Commit("a", "A")]);

        rows.Select(r => r.Lane).Should().Equal(0, 0, 0);
        rows[0].HasChildren.Should().BeFalse("the tip has no line coming from above");
        rows[0].Edges.Should().Equal(Down(0, 0, 0));
        rows[1].Edges.Should().Equal(Up(0, 0, 0), Down(0, 0, 0));
        rows[2].Edges.Should().Equal(Up(0, 0, 0));
        rows.Should().OnlyContain(r => r.LaneCount == 1 && !r.IsMerge);
    }

    [Fact]
    public void Two_branches_forked_from_a_commit_converge_into_it()
    {
        // f2 and f1 both branch from b.
        var rows = CommitGraphBuilder.Compute(
        [
            GitData.Commit("f2", "Feature 2", "b"),
            GitData.Commit("f1", "Feature 1", "b"),
            GitData.Commit("b", "Base", "r"),
            GitData.Commit("r", "Root"),
        ]);

        rows[0].Lane.Should().Be(0);
        rows[1].Lane.Should().Be(1, "a second tip opens a new lane");
        rows[1].Color.Should().Be(1);
        rows[1].Edges.Should().Equal(Up(0, 0, 0), Down(0, 0, 0), Down(1, 1, 1));
        rows[1].LaneCount.Should().Be(2);

        rows[2].Lane.Should().Be(0, "the leftmost waiting lane receives the commit");
        rows[2].Edges.Should().Equal(Up(0, 0, 0), Up(1, 0, 1), Down(0, 0, 0));
        rows[2].LaneCount.Should().Be(2);
        rows[3].LaneCount.Should().Be(1, "the freed lane is trimmed");
    }

    [Fact]
    public void Merge_commit_opens_a_lane_for_its_second_parent()
    {
        // m merges f into the line of a; both come from b.
        var rows = CommitGraphBuilder.Compute(
        [
            GitData.Commit("m", "Merge f", "a", "f"),
            GitData.Commit("a", "Main work", "b"),
            GitData.Commit("f", "Feature work", "b"),
            GitData.Commit("b", "Base"),
        ]);

        rows[0].IsMerge.Should().BeTrue();
        rows[0].Edges.Should().Equal(Down(0, 1, 1), Down(0, 0, 0));
        rows[1].Lane.Should().Be(0);
        rows[1].Edges.Should().Equal(Up(0, 0, 0), Up(1, 1, 1), Down(0, 0, 0), Down(1, 1, 1));
        rows[2].Lane.Should().Be(1);
        rows[2].Color.Should().Be(1);
        rows[3].Edges.Should().Equal(Up(0, 0, 0), Up(1, 0, 1));
    }

    [Fact]
    public void Merge_parent_already_waited_for_joins_the_existing_lane()
    {
        // t2 waits for x in lane 1; m then merges x as its second parent.
        var rows = CommitGraphBuilder.Compute(
        [
            GitData.Commit("t2", "Tip 2", "x"),
            GitData.Commit("m", "Merge", "a", "x"),
            GitData.Commit("x", "X", "a"),
            GitData.Commit("a", "A"),
        ]);

        rows[1].Lane.Should().Be(1);
        rows[1].Edges.Should().Contain(Down(1, 0, 0), "the merge line joins lane 0 that already waits for x");
        rows[1].LaneCount.Should().Be(2);
        rows[2].Lane.Should().Be(0);
    }

    [Fact]
    public void Octopus_merge_opens_one_lane_per_extra_parent()
    {
        var rows = CommitGraphBuilder.Compute(
        [
            GitData.Commit("o", "Octopus", "p1", "p2", "p3"),
            GitData.Commit("p1", "P1", "base"),
            GitData.Commit("p2", "P2", "base"),
            GitData.Commit("p3", "P3", "base"),
            GitData.Commit("base", "Base"),
        ]);

        rows[0].Edges.Where(e => e.Part == GraphEdgePart.Lower && e.From == 0).Select(e => e.To).Should().BeEquivalentTo([0, 1, 2]);
        rows[0].LaneCount.Should().Be(3);
        rows.Select(r => r.Lane).Should().Equal(0, 0, 1, 2, 0);
        rows[4].Edges.Where(e => e.Part == GraphEdgePart.Upper).Select(e => (e.From, e.To)).Should().Equal((0, 0), (1, 0), (2, 0));
    }

    [Fact]
    public void Appending_pages_gives_the_same_rows_as_one_computation()
    {
        var commits = new[]
        {
            GitData.Commit("m", "Merge", "a", "f"),
            GitData.Commit("a", "A", "b"),
            GitData.Commit("f", "F", "b"),
            GitData.Commit("b", "B", "c"),
            GitData.Commit("c", "C"),
        };

        var whole = CommitGraphBuilder.Compute(commits);
        var builder = new CommitGraphBuilder();
        var paged = commits.Take(2).Select(builder.Add).Concat(commits.Skip(2).Select(builder.Add)).ToList();

        paged.Should().Equal(whole);
        builder.MaxLaneCount.Should().Be(2);
    }

    [Fact]
    public void Lines_whose_parents_are_not_loaded_continue_to_the_bottom()
    {
        var rows = CommitGraphBuilder.Compute([GitData.Commit("b", "B", "a")]);

        rows[0].Edges.Should().Equal(Down(0, 0, 0));
    }
}
