using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Settings;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class GitHubSignInViewModelTests : IDisposable
{
    private static readonly GitHubAccount Account = new(new GitHubUser("octo", "Octo Cat", "https://avatars/octo", "https://github.com/octo"),
        ["workflow", "repo"], GitHubAuthMethod.GitCredentialManager);

    private readonly IGitHubAccountService _accounts = Substitute.For<IGitHubAccountService>();
    private readonly IShellIntegration _shell = Substitute.For<IShellIntegration>();
    private GitHubSignInViewModel? _signIn;

    public void Dispose() => _signIn?.Dispose();

    [Fact]
    public void An_existing_session_shows_the_account()
    {
        _accounts.Current.Returns(Account with { Warning = "The token lacks the workflow scope." });

        var signIn = Create();

        signIn.IsSignedIn.Should().BeTrue();
        signIn.DisplayName.Should().Be("Octo Cat");
        signIn.Login.Should().Be("octo");
        signIn.Initials.Should().Be("OC");
        signIn.AvatarUrl.Should().Be("https://avatars/octo");
        signIn.MethodText.Should().Contain("browser");
        signIn.ScopesText.Should().Be("repo, workflow");
        signIn.HasWarning.Should().BeTrue();
    }

    [Fact]
    public async Task Browser_sign_in_uses_git_credential_manager()
    {
        _accounts.SignInWithGitCredentialManagerAsync(Arg.Any<CancellationToken>()).Returns(Account);
        var signIn = Create();
        GitHubAccount? signedIn = null;
        signIn.SignedIn += (_, account) => signedIn = account;

        await signIn.SignInWithBrowserCommand.ExecuteAsync(null);

        signIn.IsSignedIn.Should().BeTrue();
        signedIn.Should().Be(Account);
        signIn.IsSigningIn.Should().BeFalse();
        signIn.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task A_rejected_token_shows_why_and_keeps_the_form()
    {
        _accounts.SignInWithTokenAsync("ghp_bad", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubAccount>(new ForgeException(ErrorKind.AuthenticationFailed, "GitHub rejected this token.", "Create a new one.")));
        var signIn = Create();
        signIn.IsTokenFormOpen = true;
        signIn.SignInWithTokenCommand.CanExecute(null).Should().BeFalse("no token was typed");

        signIn.Token = "  ghp_bad ";
        await signIn.SignInWithTokenCommand.ExecuteAsync(null);

        signIn.IsSignedIn.Should().BeFalse();
        signIn.Error!.Message.Should().Be("GitHub rejected this token.");
        signIn.Error.Hint.Should().Be("Create a new one.");
        signIn.Token.Should().Be("  ghp_bad ", "the user can fix it");
        signIn.IsTokenFormOpen.Should().BeTrue();
    }

    [Fact]
    public async Task A_valid_token_signs_in_and_is_cleared()
    {
        _accounts.SignInWithTokenAsync("ghp_good", Arg.Any<CancellationToken>()).Returns(Account with { Method = GitHubAuthMethod.PersonalAccessToken });
        var signIn = Create();
        signIn.IsTokenFormOpen = true;

        signIn.Token = "ghp_good";
        await signIn.SignInWithTokenCommand.ExecuteAsync(null);

        signIn.IsSignedIn.Should().BeTrue();
        signIn.Token.Should().BeEmpty();
        signIn.IsTokenFormOpen.Should().BeFalse();
        signIn.MethodText.Should().Contain("personal access token");
    }

    [Fact]
    public async Task The_cli_method_is_offered_only_when_the_cli_is_available()
    {
        _accounts.IsGitHubCliAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accounts.SignInWithGitHubCliAsync(Arg.Any<CancellationToken>()).Returns(Account with { Method = GitHubAuthMethod.GitHubCli });
        var signIn = Create();
        signIn.SignInWithCliCommand.CanExecute(null).Should().BeFalse();

        await signIn.InitializeAsync();

        signIn.IsGitHubCliAvailable.Should().BeTrue();
        signIn.SignInWithCliCommand.CanExecute(null).Should().BeTrue();

        await signIn.SignInWithCliCommand.ExecuteAsync(null);

        signIn.MethodText.Should().Contain("GitHub CLI");
    }

    [Fact]
    public async Task Cancelling_a_browser_sign_in_is_silent()
    {
        var started = new TaskCompletionSource();
        _accounts.SignInWithGitCredentialManagerAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return Account;
        });
        var signIn = Create();
        signIn.Token = "ghp_typed";

        var running = signIn.SignInWithBrowserCommand.ExecuteAsync(null);
        await started.Task;
        signIn.IsSigningInWithBrowser.Should().BeTrue();
        signIn.SignInWithTokenCommand.CanExecute(null).Should().BeFalse("one sign-in at a time");
        signIn.CancelSignInCommand.Execute(null);
        await running;

        signIn.IsSigningIn.Should().BeFalse();
        signIn.IsSignedIn.Should().BeFalse();
        signIn.HasError.Should().BeFalse();
    }

    [Fact]
    public void Signing_out_elsewhere_updates_the_view()
    {
        _accounts.Current.Returns(Account);
        var signIn = Create();

        _accounts.AccountChanged += Raise.Event<EventHandler<GitHubAccount?>>(_accounts, (GitHubAccount?)null);

        signIn.IsSignedIn.Should().BeFalse();
        signIn.IsSignedOut.Should().BeTrue();
    }

    [Fact]
    public void Create_token_opens_github_with_the_scopes_forgedesk_needs()
    {
        Create().CreateTokenCommand.Execute(null);

        _shell.Received(1).OpenUrl("https://github.com/settings/tokens/new?scopes=repo,workflow,read:org&description=ForgeDesk");
    }

    private GitHubSignInViewModel Create() => _signIn = new GitHubSignInViewModel(_accounts, _shell, ImmediateDispatcher.Instance);
}
