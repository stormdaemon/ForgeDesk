using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Tests.Workspace.Support;

namespace ForgeDesk.Presentation.Tests.Dashboard;

public sealed class CloneDestinationTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("https://github.com/owner/My.Repo.git", "My.Repo")]
    [InlineData("https://github.com/owner/repo/", "repo")]
    [InlineData("git@github.com:owner/repo.git", "repo")]
    [InlineData("ssh://git@host.example/team/tools.git", "tools")]
    [InlineData("", "")]
    public void The_suggested_folder_is_the_repository_name(string url, string expected)
    {
        CloneDestination.SuggestedFolderName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("forge-app", null)]
    [InlineData("My.Repo", null)]
    [InlineData("", "Enter a name for the new folder.")]
    [InlineData("a/b", "A folder name can't contain \\ / : * ? \" < > |")]
    [InlineData("what?", "A folder name can't contain \\ / : * ? \" < > |")]
    [InlineData("name.", "A folder name can't end with a period.")]
    [InlineData("CON", "“CON” is reserved by Windows. Choose another name.")]
    [InlineData("nul.txt", "“nul.txt” is reserved by Windows. Choose another name.")]
    public void Folder_names_follow_the_windows_rules(string name, string? expected)
    {
        CloneDestination.ValidateFolderName(name).Should().Be(expected);
    }

    [Fact]
    public void A_missing_or_empty_target_folder_is_fine()
    {
        Directory.CreateDirectory(_folder.Combine("empty"));

        CloneDestination.ValidateTarget(_folder.Path, "new-app").Should().BeNull();
        CloneDestination.ValidateTarget(_folder.Path, "empty").Should().BeNull();
    }

    [Fact]
    public void A_non_empty_target_folder_is_refused()
    {
        Directory.CreateDirectory(_folder.Combine("taken"));
        File.WriteAllText(_folder.Combine("taken", "README.md"), "hello");

        CloneDestination.ValidateTarget(_folder.Path, "taken").Should().Be("The folder “taken” already exists there and isn't empty. Choose another name.");
    }

    [Fact]
    public void A_file_with_the_target_name_is_refused()
    {
        File.WriteAllText(_folder.Combine("notes"), "x");

        CloneDestination.ValidateTarget(_folder.Path, "notes").Should().Contain("A file named");
    }

    [Fact]
    public void A_relative_base_folder_is_refused()
    {
        CloneDestination.ValidateTarget("relative/folder", "app").Should().Be("Choose the folder where the clone goes.");
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", true)]
    [InlineData("http://git.example.com/team/repo", true)]
    [InlineData("ssh://git@github.com/owner/repo.git", true)]
    [InlineData("git@github.com:owner/repo.git", true)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    [InlineData("-uhelp", false)]
    [InlineData("ext::sh -c touch% /tmp/pwned", false)]
    [InlineData("https://github.com", false)]
    [InlineData("owner/repo", false)]
    public void Repository_addresses_are_validated(string url, bool valid)
    {
        (CloneDestination.ValidateUrl(url) is null).Should().Be(valid);
    }

    [Fact]
    public void A_local_repository_path_is_accepted()
    {
        CloneDestination.ValidateUrl(_folder.Path).Should().BeNull();
    }

    [Fact]
    public void GitHub_addresses_are_recognized()
    {
        CloneDestination.ParseGitHub("git@github.com:stormdaemon/ForgeDesk.git")!.FullName.Should().Be("stormdaemon/ForgeDesk");
        CloneDestination.ParseGitHub("https://gitlab.com/o/r.git").Should().BeNull();
    }

    [Fact]
    public void The_default_base_folder_is_source_repos_in_the_profile()
    {
        CloneDestination.DefaultBaseFolder().Should().EndWith(Path.Combine("source", "repos"));
        CloneDestination.BaseFolderFrom("  /custom/dir ").Should().Be("/custom/dir");
        CloneDestination.BaseFolderFrom(null).Should().Be(CloneDestination.DefaultBaseFolder());
    }
}
