using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public class GitArgumentsTests
{
    [Theory]
    [InlineData("src/app.cs", "src/app.cs")]
    [InlineData("src\\app.cs", "src/app.cs")]
    [InlineData("./docs/readme.md", "docs/readme.md")]
    [InlineData("folder/", "folder")]
    [InlineData("a[1].txt", "a[1].txt")]
    public void Relative_paths_are_normalized(string input, string expected) =>
        GitArguments.RelativePath(input).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("/etc/passwd")]
    public void Paths_leaving_the_repository_are_rejected(string input)
    {
        var act = () => GitArguments.RelativePath(input);

        act.Should().Throw<ForgeException>().Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public void Absolute_windows_paths_are_rejected_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters only mean absolute paths on Windows.");

        var act = () => GitArguments.RelativePath("C:\\Windows\\win.ini");

        act.Should().Throw<ForgeException>();
    }

    [Fact]
    public void Pathspecs_are_literal_and_anchored_at_the_top() =>
        GitArguments.Pathspec("a[1]*.txt").Should().Be(":(top,literal)a[1]*.txt");

    [Fact]
    public void Batches_stay_under_the_command_line_budget()
    {
        var paths = Enumerable.Range(0, 1000).Select(i => $"some/fairly/long/folder/name/file-{i:0000}.txt").ToList();

        var batches = GitArguments.Batch(paths, maxChars: 2000).ToList();

        batches.Should().HaveCountGreaterThan(1);
        batches.SelectMany(b => b).Should().Equal(paths);
        batches.Should().OnlyContain(b => b.Sum(p => p.Length + 3) <= 2000);
    }

    [Fact]
    public void A_single_oversized_argument_still_gets_its_own_batch()
    {
        var huge = new string('x', 50);

        GitArguments.Batch([huge, "a"], maxChars: 10).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("-D")]
    [InlineData("")]
    [InlineData("line\nbreak")]
    public void Names_that_git_would_read_as_options_are_rejected(string name)
    {
        var act = () => GitArguments.Name(name, "branch name");

        act.Should().Throw<ForgeException>().Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Theory]
    [InlineData("https://user:token@github.com/o/r.git", "https://***@github.com/o/r.git")]
    [InlineData("https://ghp_abc123@github.com/o/r.git", "https://***@github.com/o/r.git")]
    [InlineData("git@github.com:o/r.git", "git@github.com:o/r.git")]
    [InlineData("fatal: unable to access 'https://x:y@host/r/': 403", "fatal: unable to access 'https://***@host/r/': 403")]
    public void Credentials_in_urls_are_masked(string text, string expected) =>
        GitRedaction.Redact(text).Should().Be(expected);
}
