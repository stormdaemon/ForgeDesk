using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Tests.Settings.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class GitHubSettingsViewModelTests : IDisposable
{
    private static readonly GitHubAccount Account = new(new GitHubUser("octo", null, "https://avatars/octo", "https://github.com/octo"), ["repo"],
        GitHubAuthMethod.PersonalAccessToken);

    private readonly SettingsHarness _harness = new();
    private GitHubSettingsViewModel? _github;

    public void Dispose()
    {
        _github?.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public async Task Signed_in_it_shows_the_rate_limit()
    {
        _harness.Accounts.Current.Returns(Account);
        _harness.GitHub.GetRateLimitAsync(Arg.Any<CancellationToken>()).Returns(new RateLimitInfo(5000, 4832, DateTimeOffset.Now.AddMinutes(30)));
        var github = Create();

        await github.ActivateAsync();

        github.SignIn.DisplayName.Should().Be("octo", "the login stands in for a missing name");
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        github.RateLimitText.Should().StartWith($"{4832.ToString("N0", culture)} of {5000.ToString("N0", culture)} requests left this hour · resets at ");
        github.RateLimitRemainingFraction.Should().BeApproximately(0.9664, 0.0001);
    }

    [Fact]
    public async Task Signed_out_nothing_is_asked_to_github()
    {
        var github = Create();

        await github.ActivateAsync();

        github.RateLimitText.Should().BeNull();
        await _harness.GitHub.DidNotReceiveWithAnyArgs().GetRateLimitAsync(default);
    }

    [Fact]
    public async Task Signing_out_asks_first()
    {
        _harness.Accounts.Current.Returns(Account);
        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false, true);
        var github = Create();

        await github.SignOutCommand.ExecuteAsync(null);

        await _harness.Accounts.DidNotReceive().SignOutAsync(Arg.Any<CancellationToken>());

        await github.SignOutCommand.ExecuteAsync(null);

        await _harness.Dialogs.Received(2).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive && o.Title == "Sign out of octo?"));
        await _harness.Accounts.Received(1).SignOutAsync(Arg.Any<CancellationToken>());
        _harness.Notifications.Received(1).Show("Signed out of GitHub", Arg.Any<string?>(), NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public void The_github_features_switch_is_saved()
    {
        var github = Create();
        github.GitHubEnabled.Should().BeTrue();

        github.GitHubEnabled = false;

        _harness.Settings.Current.GitHubEnabled.Should().BeFalse();
    }

    private GitHubSettingsViewModel Create() => _github = new GitHubSettingsViewModel(_harness.CreateStore(), _harness.Accounts, _harness.GitHub,
        _harness.Shell, _harness.Dialogs, _harness.Notifications, ImmediateDispatcher.Instance, NullLogger.Instance);
}
