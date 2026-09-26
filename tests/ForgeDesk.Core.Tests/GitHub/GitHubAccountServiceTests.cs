using System.Net;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Tests.GitHub.Fakes;
using NSubstitute;
using static ForgeDesk.Core.Tests.GitHub.Fakes.GitHubPayloads;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubAccountServiceTests
{
    private const string GcmOutput = "protocol=https\nhost=github.com\nusername=octocat\npassword=" + OAuthToken + "\npassword_expiry_utc=1790000000\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------ Personal access token

    [Fact]
    public async Task Token_sign_in_stores_the_token_and_the_login()
    {
        using var h = new AccountHarness();
        h.ServeUser();

        var account = await h.Service.SignInWithTokenAsync(ClassicToken, Ct);

        account.User.Should().Be(new GitHubUser("octocat", "The Octocat", "https://avatars.githubusercontent.com/u/583231?v=4", "https://github.com/octocat"));
        account.Method.Should().Be(GitHubAuthMethod.PersonalAccessToken);
        account.Scopes.Should().Equal("repo", "workflow", "read:org");
        account.IsVerified.Should().BeTrue();
        account.Warning.Should().BeNull();
        h.Service.Current.Should().Be(account);
        (await h.Secrets.GetAsync("ForgeDesk:github.com", Ct)).Should().Be(ClassicToken);
        h.Settings.Current.GitHubLogin.Should().Be("octocat");
        (await h.Service.GetTokenAsync(Ct)).Should().Be(ClassicToken);
        h.Events.Should().Equal(account);
        h.Api.Requests.Single().Header("Authorization").Should().Be($"Bearer {ClassicToken}");
    }

    [Fact]
    public async Task Token_sign_in_trims_what_users_paste_around_the_token()
    {
        using var h = new AccountHarness();
        h.ServeUser();

        await h.Service.SignInWithTokenAsync($"  \"{ClassicToken}\"\r\n", Ct);

        (await h.Service.GetTokenAsync(Ct)).Should().Be(ClassicToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("ghp_short")]
    [InlineData("Bearer ghp_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("glpat-xxxxxxxxxxxxxxxxxxxx")]
    public async Task Malformed_tokens_are_rejected_without_calling_github(string token)
    {
        using var h = new AccountHarness();

        var act = () => h.Service.SignInWithTokenAsync(token, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        h.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Token_rejected_by_github_is_not_stored()
    {
        using var h = new AccountHarness();
        h.RejectTokens();

        var act = () => h.Service.SignInWithTokenAsync(ClassicToken, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.AuthenticationFailed);
        error.Message.Should().Be("GitHub rejected this token.");
        h.Service.Current.Should().BeNull();
        (await h.Secrets.GetAsync(GitHubAccountService.TokenSecretKey, Ct)).Should().BeNull();
        h.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Token_sign_in_offline_reports_network_unavailable()
    {
        using var h = new AccountHarness();
        h.GoOffline();

        var act = () => h.Service.SignInWithTokenAsync(ClassicToken, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NetworkUnavailable);
        h.Service.Current.Should().BeNull();
    }

    [Fact]
    public async Task Classic_token_without_workflow_scope_gets_a_warning()
    {
        using var h = new AccountHarness();
        h.ServeUser(scopes: "repo, read:org");

        var account = await h.Service.SignInWithTokenAsync(ClassicToken, Ct);

        account.Warning.Should().Contain("'workflow'").And.NotContain("'repo'");
    }

    [Fact]
    public async Task Classic_token_without_repo_scope_gets_a_warning()
    {
        using var h = new AccountHarness();
        h.ServeUser(scopes: "public_repo");

        var account = await h.Service.SignInWithTokenAsync(ClassicToken, Ct);

        account.Scopes.Should().Equal("public_repo");
        account.Warning.Should().Contain("'repo'").And.Contain("'workflow'");
    }

    [Fact]
    public async Task Fine_grained_tokens_have_no_scopes_and_no_warning()
    {
        using var h = new AccountHarness();
        h.ServeUser(scopes: null);

        var account = await h.Service.SignInWithTokenAsync(FineGrainedToken, Ct);

        account.Scopes.Should().BeEmpty();
        account.Warning.Should().BeNull();
    }

    [Fact]
    public async Task Signing_in_again_replaces_the_previous_account()
    {
        using var h = new AccountHarness();
        h.ServeUser("octocat");
        await h.Service.SignInWithTokenAsync(ClassicToken, Ct);
        h.ServeUser("hubot");

        var account = await h.Service.SignInWithTokenAsync(OtherClassicToken, Ct);

        account.User.Login.Should().Be("hubot");
        (await h.Service.GetTokenAsync(Ct)).Should().Be(OtherClassicToken);
        h.Settings.Current.GitHubLogin.Should().Be("hubot");
    }

    // ------------------------------------------------------------ Restore

    [Fact]
    public async Task Restore_without_a_saved_token_is_signed_out()
    {
        using var h = new AccountHarness();

        var account = await h.Service.RestoreAsync(Ct);

        account.Should().BeNull();
        h.Service.Current.Should().BeNull();
        h.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_validates_the_token_and_keeps_the_sign_in_method()
    {
        using var h = new AccountHarness();
        await h.StoreSessionAsync(OAuthToken, """{"method":"GitCredentialManager","login":"octocat","scopes":["repo"]}""");
        h.ServeUser(scopes: "repo, workflow, gist");

        var account = await h.Service.RestoreAsync(Ct);

        account!.IsVerified.Should().BeTrue();
        account.Method.Should().Be(GitHubAuthMethod.GitCredentialManager);
        account.Scopes.Should().Equal("repo", "workflow", "gist");
        account.User.Name.Should().Be("The Octocat");
        h.Events.Should().Equal(account);
        (await h.Service.GetTokenAsync(Ct)).Should().Be(OAuthToken);
    }

    [Fact]
    public async Task Restore_offline_keeps_an_unverified_account_from_the_cache()
    {
        using var h = new AccountHarness();
        await h.StoreSessionAsync(ClassicToken, """{"method":"GitHubCli","login":"octocat","name":"The Octocat","avatarUrl":"https://avatars.example/1","htmlUrl":"https://github.com/octocat","scopes":["repo","workflow"]}""");
        h.GoOffline();

        var account = await h.Service.RestoreAsync(Ct);

        account!.IsVerified.Should().BeFalse();
        account.User.Login.Should().Be("octocat");
        account.User.AvatarUrl.Should().Be("https://avatars.example/1");
        account.Method.Should().Be(GitHubAuthMethod.GitHubCli);
        account.Scopes.Should().Equal("repo", "workflow");
        h.Service.Current.Should().Be(account);
        (await h.Service.GetTokenAsync(Ct)).Should().Be(ClassicToken);
        (await h.Secrets.GetAsync(GitHubAccountService.TokenSecretKey, Ct)).Should().Be(ClassicToken, "network errors never sign out");
    }

    [Fact]
    public async Task Restore_offline_falls_back_to_the_login_saved_in_settings()
    {
        using var h = new AccountHarness();
        await h.Secrets.SetAsync(GitHubAccountService.TokenSecretKey, ClassicToken, Ct);
        await h.Settings.UpdateAsync(s => s with { GitHubLogin = "mona" }, Ct);
        h.GoOffline();

        var account = await h.Service.RestoreAsync(Ct);

        account!.IsVerified.Should().BeFalse();
        account.User.Login.Should().Be("mona");
        account.User.AvatarUrl.Should().Be("https://github.com/mona.png");
        account.Method.Should().Be(GitHubAuthMethod.PersonalAccessToken);
    }

    [Fact]
    public async Task Restore_offline_without_any_cached_login_waits_for_the_network()
    {
        using var h = new AccountHarness();
        await h.Secrets.SetAsync(GitHubAccountService.TokenSecretKey, ClassicToken, Ct);
        h.GoOffline();

        var account = await h.Service.RestoreAsync(Ct);

        account.Should().BeNull();
        (await h.Secrets.GetAsync(GitHubAccountService.TokenSecretKey, Ct)).Should().Be(ClassicToken);
    }

    [Fact]
    public async Task Restore_during_a_github_outage_does_not_sign_out()
    {
        using var h = new AccountHarness();
        await h.StoreSessionAsync(ClassicToken, """{"method":"PersonalAccessToken","login":"octocat"}""");
        h.Api.OnError(HttpMethod.Get, "/user", HttpStatusCode.ServiceUnavailable, "{}");

        var account = await h.Service.RestoreAsync(Ct);

        account!.IsVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Restore_with_a_revoked_token_signs_out()
    {
        using var h = new AccountHarness();
        await h.StoreSessionAsync(ClassicToken, """{"method":"PersonalAccessToken","login":"octocat"}""");
        await h.Settings.UpdateAsync(s => s with { GitHubLogin = "octocat" }, Ct);
        h.RejectTokens();

        var account = await h.Service.RestoreAsync(Ct);

        account.Should().BeNull();
        h.Service.Current.Should().BeNull();
        (await h.Secrets.GetAsync(GitHubAccountService.TokenSecretKey, Ct)).Should().BeNull();
        (await h.Secrets.GetAsync(GitHubAccountService.SessionSecretKey, Ct)).Should().BeNull();
        h.Settings.Current.GitHubLogin.Should().BeNull();
        h.Events.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task Restore_ignores_unreadable_session_details()
    {
        using var h = new AccountHarness();
        await h.StoreSessionAsync(ClassicToken, "{not json");
        h.ServeUser();

        var account = await h.Service.RestoreAsync(Ct);

        account!.Method.Should().Be(GitHubAuthMethod.PersonalAccessToken);
        account.IsVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Sign_out_forgets_everything_and_notifies()
    {
        using var h = new AccountHarness();
        h.ServeUser();
        await h.Service.SignInWithTokenAsync(ClassicToken, Ct);

        await h.Service.SignOutAsync(Ct);

        h.Service.Current.Should().BeNull();
        (await h.Service.GetTokenAsync(Ct)).Should().BeNull();
        (await h.Secrets.GetAsync(GitHubAccountService.TokenSecretKey, Ct)).Should().BeNull();
        h.Settings.Current.GitHubLogin.Should().BeNull();
        h.Events.Should().HaveCount(2);
        h.Events[^1].Should().BeNull();
    }

    [Fact]
    public async Task Tokens_never_appear_in_logs()
    {
        using var h = new AccountHarness();
        h.ServeUser();
        await h.Service.SignInWithTokenAsync(ClassicToken, Ct);
        h.GoOffline();
        await h.Service.RestoreAsync(Ct);
        h.RejectTokens();
        await h.Service.RestoreAsync(Ct);

        h.Logs.Should().NotBeEmpty();
        h.Logs.Should().NotContain(line => line.Contains(ClassicToken, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ Git Credential Manager

    [Fact]
    public async Task Git_credential_manager_sign_in_uses_the_oauth_token_and_approves_it()
    {
        using var h = new AccountHarness();
        h.ServeUser();
        h.Processes = spec => spec.Arguments[1] == "fill" ? FakeProcessRunner.Success(GcmOutput) : FakeProcessRunner.Success(string.Empty);

        var account = await h.Service.SignInWithGitCredentialManagerAsync(Ct);

        account.Method.Should().Be(GitHubAuthMethod.GitCredentialManager);
        (await h.Service.GetTokenAsync(Ct)).Should().Be(OAuthToken);
        var fill = h.Runner.Calls[0];
        fill.FileName.Should().Be(AccountHarness.GitPath);
        fill.Arguments.Should().Equal("credential", "fill");
        fill.StandardInput.Should().Be("protocol=https\nhost=github.com\n\n");
        fill.Environment["GIT_TERMINAL_PROMPT"].Should().Be("0");
        fill.Timeout.Should().Be(TimeSpan.FromMinutes(5));
        var approve = h.Runner.Calls[1];
        approve.Arguments.Should().Equal("credential", "approve");
        approve.StandardInput.Should().Be(GcmOutput + "\n");
    }

    [Fact]
    public async Task Git_credential_manager_output_with_crlf_is_understood()
    {
        using var h = new AccountHarness();
        h.ServeUser();
        h.Processes = spec => spec.Arguments[1] == "fill" ? FakeProcessRunner.Success(GcmOutput.Replace("\n", "\r\n", StringComparison.Ordinal)) : FakeProcessRunner.Success(string.Empty);

        await h.Service.SignInWithGitCredentialManagerAsync(Ct);

        (await h.Service.GetTokenAsync(Ct)).Should().Be(OAuthToken);
    }

    [Fact]
    public async Task Git_credential_manager_needs_git()
    {
        using var h = new AccountHarness();
        h.Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<GitInstallation?>(null));

        var act = () => h.Service.SignInWithGitCredentialManagerAsync(Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.GitNotFound);
        error.Hint.Should().Contain("Git for Windows");
    }

    [Theory]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("git: 'credential-manager' is not a git command. See 'git --help'.")]
    public async Task Missing_credential_manager_is_explained(string stderr)
    {
        using var h = new AccountHarness();
        h.Processes = _ => FakeProcessRunner.Failure(128, stderr);

        var act = () => h.Service.SignInWithGitCredentialManagerAsync(Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.ToolNotFound);
        error.Message.Should().Contain("Git Credential Manager");
        error.Hint.Should().Contain("Git for Windows").And.Contain("Git Credential Manager");
        error.Detail.Should().Be(stderr);
    }

    [Fact]
    public async Task Cancelled_browser_sign_in_is_reported_as_cancelled()
    {
        using var h = new AccountHarness();
        h.Processes = _ => FakeProcessRunner.Failure(1, "fatal: User cancelled the authentication prompt.");

        var act = () => h.Service.SignInWithGitCredentialManagerAsync(Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.Cancelled);
    }

    [Fact]
    public async Task Slow_browser_sign_in_times_out()
    {
        using var h = new AccountHarness();
        h.Processes = _ => FakeProcessRunner.TimedOut();

        var act = () => h.Service.SignInWithGitCredentialManagerAsync(Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.Timeout);
    }

    [Fact]
    public async Task Stale_credential_manager_token_is_rejected_so_the_next_attempt_prompts()
    {
        using var h = new AccountHarness();
        h.RejectTokens();
        h.Processes = spec => spec.Arguments[1] == "fill" ? FakeProcessRunner.Success(GcmOutput) : FakeProcessRunner.Success(string.Empty);

        var act = () => h.Service.SignInWithGitCredentialManagerAsync(Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AuthenticationFailed);
        h.Runner.Calls.Select(c => c.Arguments[1]).Should().Equal("fill", "reject");
        h.Service.Current.Should().BeNull();
    }

    // ------------------------------------------------------------ GitHub CLI

    [Fact]
    public async Task GitHub_cli_sign_in_reuses_its_token()
    {
        using var h = new AccountHarness();
        h.ServeUser();
        h.Processes = _ => FakeProcessRunner.Success(OAuthToken + "\n");

        var account = await h.Service.SignInWithGitHubCliAsync(Ct);

        account.Method.Should().Be(GitHubAuthMethod.GitHubCli);
        (await h.Service.GetTokenAsync(Ct)).Should().Be(OAuthToken);
        var call = h.Runner.Calls.Single();
        call.FileName.Should().Be(Path.Combine("tools", "gh.exe"));
        call.Arguments.Should().Equal("auth", "token", "--hostname", "github.com");
    }

    [Fact]
    public async Task Missing_github_cli_is_explained()
    {
        using var h = new AccountHarness();
        h.CliLocator.Path = null;

        (await h.Service.IsGitHubCliAvailableAsync(Ct)).Should().BeFalse();
        var act = () => h.Service.SignInWithGitHubCliAsync(Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.ToolNotFound);
        error.Hint.Should().Contain("winget install GitHub.cli");
        h.Runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Installed_github_cli_is_available()
    {
        using var h = new AccountHarness();

        (await h.Service.IsGitHubCliAvailableAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Github_cli_that_is_not_signed_in_is_explained()
    {
        using var h = new AccountHarness();
        h.Processes = _ => FakeProcessRunner.Failure(1, "no oauth token found for github.com");

        var act = () => h.Service.SignInWithGitHubCliAsync(Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.AuthenticationRequired);
        error.Hint.Should().Contain("gh auth login");
    }

    [Fact]
    public async Task Github_cli_token_rejected_by_github_is_explained()
    {
        using var h = new AccountHarness();
        h.RejectTokens();
        h.Processes = _ => FakeProcessRunner.Success(OAuthToken);

        var act = () => h.Service.SignInWithGitHubCliAsync(Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.AuthenticationFailed);
        error.Message.Should().Contain("GitHub CLI");
    }
}
