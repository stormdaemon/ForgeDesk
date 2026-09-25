using ForgeDesk.Core.GitHub;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubRemoteParserTests
{
    [Theory]
    [InlineData("https://github.com/octo/hello-world.git")]
    [InlineData("https://github.com/octo/hello-world")]
    [InlineData("https://github.com/octo/hello-world/")]
    [InlineData("https://user@github.com/octo/hello-world.git")]
    [InlineData("git@github.com:octo/hello-world.git")]
    [InlineData("ssh://git@github.com/octo/hello-world.git")]
    [InlineData("ssh://git@ssh.github.com:443/octo/hello-world.git")]
    [InlineData("git://github.com/octo/hello-world.git")]
    public void Parses_common_remote_forms(string url)
    {
        var parsed = GitHubRemoteParser.Parse(url);
        parsed.Should().NotBeNull();
        parsed!.Owner.Should().Be("octo");
        parsed.Name.Should().Be("hello-world");
    }

    [Fact]
    public void Keeps_dots_in_repository_names() =>
        GitHubRemoteParser.Parse("https://github.com/octo/site.github.io.git")!.Name.Should().Be("site.github.io");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://gitlab.com/octo/hello.git")]
    [InlineData("https://github.com/octo")]
    [InlineData("C:\\repos\\hello")]
    public void Rejects_non_github_urls(string? url) => GitHubRemoteParser.Parse(url).Should().BeNull();
}
