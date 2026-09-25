using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using static ForgeDesk.Core.Tests.GitHub.Fakes.GitHubPayloads;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubTokensTests
{
    [Theory]
    [InlineData(ClassicToken)]
    [InlineData(FineGrainedToken)]
    [InlineData(OAuthToken)]
    [InlineData("ghu_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public void Known_token_shapes_are_accepted(string token) =>
        GitHubTokens.Normalize(token).Should().Be(token);

    [Theory]
    [InlineData("ghp_abc")]
    [InlineData("ghp_16C7e42F292c6912E7710c838347Ae178B4a extra")]
    [InlineData("ghs_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("password123")]
    public void Other_strings_are_rejected(string token)
    {
        var act = () => GitHubTokens.Normalize(token);

        act.Should().Throw<ForgeException>().Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Theory]
    [InlineData(ClassicToken, "repo, workflow", null)]
    [InlineData(ClassicToken, "repo", "workflow")]
    [InlineData(ClassicToken, "workflow", "private repositories")]
    [InlineData(OAuthToken, "", "private repositories")]
    [InlineData(FineGrainedToken, "", null)]
    [InlineData("ghu_16C7e42F292c6912E7710c838347Ae178B4a", "", null)]
    public void Missing_scopes_produce_a_warning_for_classic_tokens(string token, string scopes, string? expectedFragment)
    {
        var warning = GitHubTokens.DescribeMissingScopes(token, GitHubScopes.Parse(scopes));

        if (expectedFragment is null)
        {
            warning.Should().BeNull();
        }
        else
        {
            warning.Should().Contain(expectedFragment);
        }
    }

    [Theory]
    [InlineData("repo", "public_repo", true)]
    [InlineData("repo", "repo:status", true)]
    [InlineData("public_repo", "repo", false)]
    [InlineData("admin:org", "read:org", true)]
    [InlineData("write:org", "read:org", true)]
    [InlineData("read:org", "admin:org", false)]
    [InlineData("REPO", "repo", true)]
    public void Broader_scopes_imply_narrower_ones(string granted, string required, bool expected) =>
        GitHubScopes.Satisfies([granted], required).Should().Be(expected);

    [Fact]
    public void Missing_scope_is_only_computed_for_scoped_tokens()
    {
        GitHubScopes.FindMissing(["repo"], ["gist"]).Should().Be("repo");
        GitHubScopes.FindMissing(["repo", "public_repo"], ["public_repo"]).Should().BeNull();
        GitHubScopes.FindMissing(["repo"], null).Should().BeNull("fine-grained tokens send no scope header");
        GitHubScopes.FindMissing([], ["gist"]).Should().BeNull();
    }

    [Theory]
    [InlineData("d73a4a", "#d73a4a")]
    [InlineData("#A2EEEF", "#a2eeef")]
    [InlineData("", "#ededed")]
    [InlineData(null, "#ededed")]
    [InlineData("red", "#ededed")]
    public void Label_colors_are_normalized(string? color, string expected) =>
        GitHubMapper.NormalizeColor(color).Should().Be(expected);
}
