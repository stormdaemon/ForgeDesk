using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public class GitStatusParserTests
{
    private static string Z(params string[] records) => string.Join('\0', records) + "\0\n";

    [Fact]
    public void Parses_branch_headers()
    {
        var status = GitStatusParser.Parse(Z(
            "# branch.oid 1111111111111111111111111111111111111111",
            "# branch.head feature/login",
            "# branch.upstream origin/feature/login",
            "# branch.ab +3 -2",
            "# stash 4"));

        status.Branch.Should().Be("feature/login");
        status.HeadSha.Should().Be("1111111111111111111111111111111111111111");
        status.Upstream.Should().Be("origin/feature/login");
        status.Ahead.Should().Be(3);
        status.Behind.Should().Be(2);
        status.StashCount.Should().Be(4);
        status.IsDetached.Should().BeFalse();
        status.IsUnborn.Should().BeFalse();
        status.IsClean.Should().BeTrue();
    }

    [Fact]
    public void Detached_and_unborn_heads()
    {
        var detached = GitStatusParser.Parse(Z("# branch.oid 2222222222222222222222222222222222222222", "# branch.head (detached)"));
        var unborn = GitStatusParser.Parse(Z("# branch.oid (initial)", "# branch.head main"));

        detached.IsDetached.Should().BeTrue();
        detached.Branch.Should().BeNull();
        detached.HeadSha.Should().NotBeNull();
        unborn.IsUnborn.Should().BeTrue();
        unborn.HeadSha.Should().BeNull();
        unborn.Branch.Should().Be("main");
    }

    [Fact]
    public void Ordinary_entries_map_both_sides()
    {
        var status = GitStatusParser.Parse(Z(
            "1 M. N... 100644 100644 100644 aaa bbb src/staged.cs",
            "1 .M N... 100644 100644 100644 aaa aaa src/unstaged.cs",
            "1 MM N... 100644 100644 100644 aaa bbb both.cs",
            "1 A. N... 000000 100644 100644 000 bbb added.cs",
            "1 .D N... 100644 100644 000000 aaa aaa gone.cs",
            "1 .A N... 000000 000000 100644 000 000 intent.cs",
            "1 T. N... 120000 100644 100644 aaa bbb link",
            "1 .M N... 100644 100644 100644 aaa aaa name with spaces.txt"));

        status.Entries.Should().HaveCount(8);
        status.Entries[0].Should().Match<GitStatusEntry>(e => e.IndexState == GitFileState.Modified && e.WorkTreeState == GitFileState.Unmodified);
        status.Entries[1].Should().Match<GitStatusEntry>(e => e.IndexState == GitFileState.Unmodified && e.WorkTreeState == GitFileState.Modified);
        status.Entries[2].HasStagedChanges.Should().BeTrue();
        status.Entries[2].HasUnstagedChanges.Should().BeTrue();
        status.Entries[3].IndexState.Should().Be(GitFileState.Added);
        status.Entries[4].WorkTreeState.Should().Be(GitFileState.Deleted);
        status.Entries[5].WorkTreeState.Should().Be(GitFileState.Added);
        status.Entries[6].IndexState.Should().Be(GitFileState.TypeChanged);
        status.Entries[7].Path.Should().Be("name with spaces.txt");
    }

    [Fact]
    public void Rename_entries_take_the_original_path_from_the_next_record()
    {
        var status = GitStatusParser.Parse(Z(
            "2 R. N... 100644 100644 100644 aaa aaa R100 docs/new name.md", "docs/old name.md",
            "? after.txt"));

        status.Entries.Should().HaveCount(2);
        status.Entries[0].Path.Should().Be("docs/new name.md");
        status.Entries[0].OriginalPath.Should().Be("docs/old name.md");
        status.Entries[0].IndexState.Should().Be(GitFileState.Renamed);
        status.Entries[1].Path.Should().Be("after.txt");
    }

    [Fact]
    public void Unmerged_entries_are_conflicted_on_both_sides()
    {
        var status = GitStatusParser.Parse(Z(
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflict.txt",
            "u AA N... 000000 100644 100644 100644 000 bbb ccc both-added.txt"));

        status.Entries.Should().OnlyContain(e => e.IsConflicted && e.IndexState == GitFileState.Conflicted && e.WorkTreeState == GitFileState.Conflicted);
        status.Conflicted.Should().HaveCount(2);
        status.Staged.Should().BeEmpty();
        status.Unstaged.Should().BeEmpty();
    }

    [Fact]
    public void Untracked_entries_keep_unusual_names_verbatim()
    {
        var status = GitStatusParser.Parse(Z("? ünïcödé/файл.txt", "? we\"ird\\name.txt"));

        status.Entries.Select(e => e.Path).Should().Equal("ünïcödé/файл.txt", "we\"ird\\name.txt");
        status.Entries.Should().OnlyContain(e => e.IsUntracked && !e.HasStagedChanges);
    }

    [Fact]
    public void Malformed_records_are_ignored()
    {
        var status = GitStatusParser.Parse(Z("1 M.", "2 R.", "u UU", "# nonsense", "1 M. N... 100644 100644 100644 aaa bbb ok.txt"));

        status.Entries.Should().ContainSingle().Which.Path.Should().Be("ok.txt");
    }
}
