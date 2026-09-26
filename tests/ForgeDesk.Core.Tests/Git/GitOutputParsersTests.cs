using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public class GitOutputParsersTests
{
    private const char Us = '\x1f';
    private const char Rs = '\x1e';

    [Fact]
    public void Log_records_parse_every_field()
    {
        var output =
            $"{Rs}aaaa{Us}bbbb cccc{Us}Ada{Us}ada@example.com{Us}2026-01-02T03:04:05+01:00{Us}Grace{Us}grace@example.com{Us}2026-01-03T00:00:00+00:00{Us}HEAD -> main, origin/main, tag: v1.0{Us}Merge feature\n\nBody line 1\n\nBody line 3\n\n" +
            $"{Rs}dddd{Us}{Us}Ada{Us}ada@example.com{Us}2026-01-01T00:00:00+00:00{Us}Ada{Us}ada@example.com{Us}2026-01-01T00:00:00+00:00{Us}{Us}Initial\n\n";

        var commits = GitLogParser.Parse(output);

        commits.Should().HaveCount(2);
        var merge = commits[0];
        merge.Sha.Should().Be("aaaa");
        merge.Parents.Should().Equal("bbbb", "cccc");
        merge.IsMerge.Should().BeTrue();
        merge.Author.Should().Be(new GitSignature("Ada", "ada@example.com", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1))));
        merge.Committer!.Name.Should().Be("Grace");
        merge.Refs.Should().Equal("HEAD -> main", "origin/main", "tag: v1.0");
        merge.Subject.Should().Be("Merge feature");
        merge.Body.Should().Be("Body line 1\n\nBody line 3");
        commits[1].Parents.Should().BeEmpty();
        commits[1].Refs.Should().BeEmpty();
        commits[1].Subject.Should().Be("Initial");
        commits[1].Body.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Title", "Title", "")]
    [InlineData("Title\n\nBody", "Title", "Body")]
    [InlineData("Title\nsecond line without blank\n", "Title", "second line without blank")]
    [InlineData("\n\nTitle  \r\n\r\n\r\nBody 1\r\nBody 2\r\n\r\n", "Title", "Body 1\nBody 2")]
    public void Messages_split_into_subject_and_body(string message, string subject, string body) =>
        GitLogParser.SplitMessage(message).Should().Be((subject, body));

    [Fact]
    public void Branch_lines_parse_tracking_and_skip_symbolic_heads()
    {
        var output = string.Join('\n',
            $"refs/heads/main{Us}*{Us}refs/remotes/origin/main{Us}ahead 2, behind 1{Us}1111{Us}2026-03-01T10:00:00+00:00{Us}Ada{Us}{Us}Fix bug",
            $"refs/heads/old{Us} {Us}refs/remotes/origin/old{Us}gone{Us}2222{Us}2026-02-01T10:00:00+00:00{Us}Ada{Us}{Us}Old work",
            $"refs/heads/local{Us} {Us}{Us}{Us}3333{Us}2026-02-02T10:00:00+00:00{Us}Ada{Us}{Us}Local only",
            $"refs/remotes/origin/HEAD{Us} {Us}{Us}{Us}1111{Us}2026-03-01T10:00:00+00:00{Us}Ada{Us}refs/remotes/origin/main{Us}Fix bug",
            $"refs/remotes/team/eu/main{Us} {Us}{Us}{Us}4444{Us}2026-03-02T10:00:00+00:00{Us}Bob{Us}{Us}Remote work") + "\n";

        var branches = GitRefParsers.ParseBranches(output, ["origin", "team/eu"]);

        branches.Select(b => b.Name).Should().Equal("main", "old", "local", "team/eu/main");
        branches[0].Should().Match<GitBranch>(b => b.IsCurrent && b.Upstream == "origin/main" && b.Ahead == 2 && b.Behind == 1 && !b.UpstreamGone);
        branches[0].TipSubject.Should().Be("Fix bug");
        branches[0].TipDate.Should().Be(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        branches[1].UpstreamGone.Should().BeTrue();
        branches[2].Upstream.Should().BeNull();
        branches[3].Should().Match<GitBranch>(b => b.IsRemote && b.RemoteName == "team/eu" && b.FullName == "refs/remotes/team/eu/main");
    }

    [Theory]
    [InlineData("", 0, 0, false)]
    [InlineData("ahead 3", 3, 0, false)]
    [InlineData("behind 7", 0, 7, false)]
    [InlineData("ahead 1, behind 12", 1, 12, false)]
    [InlineData("gone", 0, 0, true)]
    public void Tracking_summaries(string track, int ahead, int behind, bool gone) =>
        GitRefParsers.ParseTrack(track).Should().Be((ahead, behind, gone));

    [Fact]
    public void Tags_distinguish_annotated_and_lightweight()
    {
        const string signature = "-----BEGIN PGP SIGNATURE-----\nabc\n-----END PGP SIGNATURE-----\n";
        var output =
            $"refs/tags/v2.0{Us}tag{Us}tagobj{Us}commit2{Us}2026-05-01T12:00:00+02:00{Us}{Us}Release 2\n\nHighlights\n#42 kept\n{Rs}\n" +
            $"refs/tags/v1.0{Us}commit{Us}commit1{Us}{Us}2026-04-01T12:00:00+02:00{Us}{Us}Some commit subject\n{Rs}\n" +
            $"refs/tags/v3.0{Us}tag{Us}tagobj3{Us}commit3{Us}2026-06-01T12:00:00+02:00{Us}{signature}{Us}Signed release\n{signature}{Rs}\n";

        var tags = GitRefParsers.ParseTags(output);

        tags.Should().HaveCount(3);
        tags[0].Should().Be(new GitTag("v2.0", "commit2", new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.FromHours(2)), "Release 2\n\nHighlights\n#42 kept", true));
        tags[1].Should().Be(new GitTag("v1.0", "commit1", new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.FromHours(2)), null, false));
        tags[2].Message.Should().Be("Signed release");
    }

    [Fact]
    public void Stash_list_lines()
    {
        var output = $"stash@{{0}}{Us}2026-06-01T08:00:00+00:00{Us}On main: wip ui\nstash@{{1}}{Us}2026-05-31T08:00:00+00:00{Us}WIP on main: 1a2b3c4 Fix\n";

        var stashes = GitRefParsers.ParseStashes(output);

        stashes.Should().Equal(
            new GitStash(0, "stash@{0}", "On main: wip ui", new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero)),
            new GitStash(1, "stash@{1}", "WIP on main: 1a2b3c4 Fix", new DateTimeOffset(2026, 5, 31, 8, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Remote_lines_group_fetch_and_push_urls()
    {
        var output = "origin\thttps://github.com/octo/app.git (fetch)\norigin\tgit@github.com:octo/app.git (push)\nupstream\tC:\\src\\mirror (fetch)\nupstream\tC:\\src\\mirror (push)\n";

        GitRefParsers.ParseRemotes(output).Should().Equal(
            new GitRemote("origin", "https://github.com/octo/app.git", "git@github.com:octo/app.git"),
            new GitRemote("upstream", "C:\\src\\mirror", "C:\\src\\mirror"));
    }

    [Fact]
    public void Numstat_and_name_status_are_joined_including_renames_and_binaries()
    {
        var numstat = "3\t1\tsrc/app.cs\0-\t-\tlogo.png\0" + "0\t0\t\0old.txt\0new.txt\0" + "0\t5\tgone.txt\0\n";
        var nameStatus = "M\0src/app.cs\0A\0logo.png\0R100\0old.txt\0new.txt\0D\0gone.txt\0\n";

        var files = GitRefParsers.ParseFileChanges(numstat, nameStatus);

        files.Should().Equal(
            new GitFileChange("src/app.cs", null, GitFileState.Modified, 3, 1, false),
            new GitFileChange("logo.png", null, GitFileState.Added, 0, 0, true),
            new GitFileChange("new.txt", "old.txt", GitFileState.Renamed, 0, 0, false),
            new GitFileChange("gone.txt", null, GitFileState.Deleted, 0, 5, false));
    }

    [Fact]
    public void Path_lines_are_unquoted_deduplicated_and_ignore_the_truncation_marker()
    {
        var output = "a.txt\n\"tab\\tname.txt\"\nünï.txt\na.txt\n" + GitCli.OutputTruncatedMarker + "\n";

        GitRefParsers.ParsePathLines(output).Should().Equal("a.txt", "tab\tname.txt", "ünï.txt");
    }

    [Theory]
    [InlineData("origin/main", "origin")]
    [InlineData("team/eu/feature/x", "team/eu")]
    [InlineData("unknown/branch", "unknown")]
    [InlineData("nobranch", null)]
    public void Remote_of_a_remote_branch_prefers_the_longest_known_remote(string branch, string? expected) =>
        GitRefParsers.RemoteOf(branch, ["origin", "team", "team/eu"]).Should().Be(expected);
}
