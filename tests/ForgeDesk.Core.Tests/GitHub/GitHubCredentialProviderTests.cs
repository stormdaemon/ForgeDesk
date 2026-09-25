using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Tests.GitHub.Fakes;
using static ForgeDesk.Core.Tests.GitHub.Fakes.GitHubPayloads;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubCredentialProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://github.com/octo/app.git")]
    [InlineData("https://github.com/octo/app")]
    [InlineData("https://GitHub.com/octo/app.git")]
    [InlineData("https://octocat@github.com/octo/app.git")]
    [InlineData("https://www.github.com/octo/app.git")]
    public async Task GitHub_https_remotes_get_the_signed_in_token(string remote)
    {
        var (provider, _) = Create(signedIn: true);

        var credential = await provider.GetCredentialAsync(remote, Ct);

        credential.Should().Be(new GitCredential("x-access-token", ClassicToken));
    }

    [Theory]
    [InlineData("git@github.com:octo/app.git")]
    [InlineData("ssh://git@github.com/octo/app.git")]
    [InlineData("ssh://git@ssh.github.com:443/octo/app.git")]
    [InlineData("http://github.com/octo/app.git")]
    [InlineData("https://github.com:8443/octo/app.git")]
    [InlineData("https://gitlab.com/octo/app.git")]
    [InlineData("https://github.com.evil.example/octo/app.git")]
    [InlineData("https://user:secret@github.com/octo/app.git")]
    [InlineData(@"C:\repos\app")]
    [InlineData("")]
    public async Task Other_remotes_are_left_to_git(string remote)
    {
        var (provider, _) = Create(signedIn: true);

        (await provider.GetCredentialAsync(remote, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Nothing_is_provided_when_signed_out()
    {
        var (provider, _) = Create(signedIn: false);

        (await provider.GetCredentialAsync("https://github.com/octo/app.git", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Nothing_is_provided_when_the_user_disabled_it()
    {
        var (provider, settings) = Create(signedIn: true);
        await settings.UpdateAsync(s => s with { UseGitHubTokenForGit = false }, Ct);

        (await provider.GetCredentialAsync("https://github.com/octo/app.git", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Nothing_is_provided_when_github_integration_is_off()
    {
        var (provider, settings) = Create(signedIn: true);
        await settings.UpdateAsync(s => s with { GitHubEnabled = false }, Ct);

        (await provider.GetCredentialAsync("https://github.com/octo/app.git", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task The_current_token_is_used_after_an_account_switch()
    {
        var session = new GitHubSession();
        var provider = new GitHubCredentialProvider(session, new FakeSettingsService());
        session.Set(ClassicToken, Account("octocat"));
        session.Set(OtherClassicToken, Account("hubot"));

        var credential = await provider.GetCredentialAsync("https://github.com/octo/app.git", Ct);

        credential!.Password.Should().Be(OtherClassicToken);
    }

    private static (GitHubCredentialProvider Provider, FakeSettingsService Settings) Create(bool signedIn)
    {
        var session = new GitHubSession();
        if (signedIn)
        {
            session.Set(ClassicToken, Account("octocat"));
        }

        var settings = new FakeSettingsService();
        return (new GitHubCredentialProvider(session, settings), settings);
    }

    private static GitHubAccount Account(string login) =>
        new(new GitHubUser(login, null, string.Empty, $"https://github.com/{login}"), [], GitHubAuthMethod.PersonalAccessToken);
}
