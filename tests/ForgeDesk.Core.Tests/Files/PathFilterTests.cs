using ForgeDesk.Core.Files;

namespace ForgeDesk.Core.Tests.Files;

public class PathFilterTests
{
    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "src/deep/Program.cs", true)]
    [InlineData("*.cs", "src/Program.csproj", false)]
    [InlineData("src/**", "src/a/b.txt", true)]
    [InlineData("src/**", "lib/src/b.txt", false)]
    [InlineData("src", "src/a.txt", true)]
    [InlineData("src", "lib/src/a.txt", true)]
    [InlineData("/src", "lib/src/a.txt", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/sub/a.cs", false)]
    [InlineData("test?.py", "tests/test1.py", true)]
    [InlineData("*.[ch]", "lib/x.h", true)]
    [InlineData("*.[!ch]", "lib/x.h", false)]
    [InlineData("*.cs, *.xaml", "Views/Main.xaml", true)]
    [InlineData("*.cs; *.xaml", "README.md", false)]
    [InlineData("!*.min.js", "dist/app.min.js", false)]
    [InlineData("!*.min.js", "src/app.js", true)]
    [InlineData("*.js, !vendor", "vendor/lib.js", false)]
    [InlineData("*.js, !vendor", "src/lib.js", true)]
    [InlineData(@"src\app\*.ts", "src/app/main.ts", true)]
    public void Matches_like_git_pathspecs(string filter, string path, bool expected)
    {
        PathFilter.Parse(filter, ignoreCase: false).IsMatch(path).Should().Be(expected);
    }

    [Fact]
    public void Empty_filter_matches_everything()
    {
        PathFilter.Parse("  ").IsEmpty.Should().BeTrue();
        PathFilter.Parse(null).IsMatch("any/file.txt").Should().BeTrue();
    }

    [Fact]
    public void Case_sensitivity_is_configurable()
    {
        PathFilter.Parse("*.CS", ignoreCase: true).IsMatch("a.cs").Should().BeTrue();
        PathFilter.Parse("*.CS", ignoreCase: false).IsMatch("a.cs").Should().BeFalse();
    }

    [Fact]
    public void Produces_equivalent_git_pathspecs()
    {
        var pathspecs = PathFilter.Parse("*.cs, !obj", ignoreCase: false).ToGitPathspecs().ToList();

        pathspecs.Should().Equal(":(glob)**/*.cs", ":(glob)**/*.cs/**", ":(exclude,glob)**/obj", ":(exclude,glob)**/obj/**");
        PathFilter.Parse("*.cs", ignoreCase: true).ToGitPathspecs().First().Should().Be(":(glob,icase)**/*.cs");
    }
}
