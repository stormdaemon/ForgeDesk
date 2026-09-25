using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public class GitProgressParserTests
{
    [Theory]
    [InlineData("Receiving objects:  45% (450/1000), 1.20 MiB | 2.00 MiB/s", "Receiving objects", 45)]
    [InlineData("Resolving deltas: 100% (12/12), done.", "Resolving deltas", 100)]
    [InlineData("Writing objects:   3% (1/30)", "Writing objects", 3)]
    [InlineData("remote: Counting objects: 100% (3/3), done.", "Counting objects", 100)]
    [InlineData("remote: Compressing objects:  50% (1/2)", "Compressing objects", 50)]
    [InlineData("Updating files:  50% (1/2)", "Updating files", 50)]
    [InlineData("Enumerating objects: 5, done.", "Enumerating objects", null)]
    [InlineData("remote: Enumerating objects: 1234, done.", "Enumerating objects", null)]
    public void Parses_progress_lines(string line, string stage, int? percent)
    {
        GitProgressParser.TryParse(line, out var progress).Should().BeTrue();

        progress.Stage.Should().Be(stage);
        progress.Percent.Should().Be(percent);
        progress.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Cloning into 'app'...")]
    [InlineData("fatal: unable to access 'https://x/': Could not resolve host: x")]
    [InlineData("CONFLICT (content): Merge conflict in a.txt")]
    [InlineData("Note: switching to 'abc'.")]
    [InlineData("Your local changes to the following files would be overwritten by checkout:")]
    [InlineData("To https://github.com/octo/app.git")]
    public void Ignores_other_lines(string line) =>
        GitProgressParser.TryParse(line, out _).Should().BeFalse();
}

public class GitQuotingTests
{
    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("\"tab\\tname.txt\"", "tab\tname.txt")]
    [InlineData("\"we\\\"ird.txt\"", "we\"ird.txt")]
    [InlineData("\"back\\\\slash\"", "back\\slash")]
    [InlineData("\"caf\\303\\251\"", "café")]
    [InlineData("\"ünï\"", "ünï")]
    public void Unquotes_c_style_paths(string quoted, string expected) =>
        GitQuoting.Unquote(quoted).Should().Be(expected);

    [Theory]
    [InlineData("plain name.txt")]
    [InlineData("tab\tname.txt")]
    [InlineData("we\"ird\\x")]
    [InlineData("line\nbreak")]
    [InlineData("ünïcödé.txt")]
    public void Quote_round_trips(string path) =>
        GitQuoting.Unquote(GitQuoting.Quote(path)).Should().Be(path);

    [Fact]
    public void Only_special_characters_are_quoted()
    {
        GitQuoting.Quote("ünï space.txt").Should().Be("ünï space.txt");
        GitQuoting.Quote("a\u0001b").Should().Be("\"a\\001b\"");
    }

    [Fact]
    public void Malformed_quotes_are_left_alone() =>
        GitQuoting.Unquote("\"broken\\q\"").Should().Be("\"broken\\q\"");
}
