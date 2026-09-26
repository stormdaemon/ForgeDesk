using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Tests.Settings.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class GitSettingsViewModelTests : IDisposable
{
    private static readonly GitInstallation Installed = new("/usr/bin/git", "2.46.0");

    private readonly SettingsHarness _harness = new();

    public GitSettingsViewModelTests()
    {
        _harness.Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(Installed);
        _harness.Git.GetIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new GitIdentity("Ada Lovelace", "ada@example.com"));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Loading_shows_the_detected_git_and_the_global_identity()
    {
        var git = Create();

        await git.ActivateAsync();

        git.IsGitFound.Should().BeTrue();
        git.GitStatusText.Should().Be("Git 2.46.0");
        git.GitStatusTone.Should().Be(StatusTone.Success);
        git.GitPath.Should().Be("/usr/bin/git");
        git.AuthorName.Should().Be("Ada Lovelace");
        git.AuthorEmail.Should().Be("ada@example.com");
        await _harness.Git.Received(1).GetIdentityAsync(GitSettingsViewModel.IdentityProbeDirectory(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_git_offers_the_download()
    {
        _harness.Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns((GitInstallation?)null);
        var git = Create();

        await git.ActivateAsync();

        git.IsGitFound.Should().BeFalse();
        git.GitStatusTone.Should().Be(StatusTone.Danger);
        git.DownloadGitCommand.Execute(null);
        _harness.Shell.Received(1).OpenUrl(GitSettingsViewModel.DownloadGitUrl);
        await _harness.Git.DidNotReceiveWithAnyArgs().GetIdentityAsync(default!, default);
    }

    [Fact]
    public async Task A_chosen_git_that_works_is_kept()
    {
        var custom = new GitInstallation("/opt/git/bin/git", "2.47.0");
        _harness.Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(_ => _harness.Settings.Current.GitExecutablePath is null ? Installed : custom);
        var git = Create();
        await git.ActivateAsync();

        var used = await git.UseGitPathAsync("/opt/git/bin/git");

        used.Should().BeTrue();
        _harness.Settings.Current.GitExecutablePath.Should().Be("/opt/git/bin/git");
        git.HasCustomGitPath.Should().BeTrue();
        git.GitVersion.Should().Be("2.47.0");
        git.ResetGitPathCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_chosen_file_that_is_not_git_is_refused_and_the_previous_choice_restored()
    {
        _harness.Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(_ => _harness.Settings.Current.GitExecutablePath is null ? Installed : null);
        var git = Create();
        await git.ActivateAsync();

        var used = await git.UseGitPathAsync("/home/me/notes.txt");

        used.Should().BeFalse();
        _harness.Settings.Current.GitExecutablePath.Should().BeNull();
        git.GitPathError.Should().Contain("isn't a working Git program");
        git.IsGitFound.Should().BeTrue("the automatic Git is used again");
        git.HasCustomGitPath.Should().BeFalse();
    }

    [Fact]
    public async Task Browse_uses_the_file_picker()
    {
        _harness.Dialogs.PickFilesAsync(Arg.Any<string>(), Arg.Any<string?>(), false).Returns(["/opt/git/bin/git"]);
        var git = Create();
        await git.ActivateAsync();

        await git.BrowseGitCommand.ExecuteAsync(null);

        _harness.Settings.Current.GitExecutablePath.Should().Be("/opt/git/bin/git");
    }

    [Fact]
    public async Task Reset_goes_back_to_the_detected_git()
    {
        _harness.Settings.ChangeExternally(s => s with { GitExecutablePath = "/opt/git/bin/git" });
        var git = Create();
        await git.ActivateAsync();

        await git.ResetGitPathCommand.ExecuteAsync(null);

        _harness.Settings.Current.GitExecutablePath.Should().BeNull();
        git.HasCustomGitPath.Should().BeFalse();
    }

    [Fact]
    public async Task A_valid_identity_is_written_to_the_global_configuration()
    {
        var git = Create();
        await git.ActivateAsync();

        git.AuthorName = "Grace Hopper";
        git.AuthorEmail = "grace@example.com";

        await _harness.Git.Received(1).SetGlobalIdentityAsync("Grace Hopper", "ada@example.com", Arg.Any<CancellationToken>());
        await _harness.Git.Received(1).SetGlobalIdentityAsync("Grace Hopper", "grace@example.com", Arg.Any<CancellationToken>());
        git.IdentityStatus.Should().Contain("Saved");
    }

    [Theory]
    [InlineData("", "ada@example.com", true, false)]
    [InlineData("Ada", "not-an-email", false, true)]
    [InlineData("Ada", "@example.com", false, true)]
    [InlineData("Ada", "ada@", false, true)]
    public async Task An_invalid_identity_is_not_saved(string name, string email, bool nameError, bool emailError)
    {
        var git = Create();
        await git.ActivateAsync();

        git.AuthorName = name;
        git.AuthorEmail = email;

        git.HasAuthorNameError.Should().Be(nameError);
        git.HasAuthorEmailError.Should().Be(emailError);
        await _harness.Git.DidNotReceive().SetGlobalIdentityAsync(name, email, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pull_strategy_and_fetch_settings_are_saved()
    {
        var git = Create();
        await git.ActivateAsync();

        git.SelectedPullStrategy = git.PullStrategies.Single(o => o.Value == PullStrategy.Rebase);
        git.AutoFetch = false;
        git.AutoFetchInterval = 30;
        git.UseGitHubSignInForGit = false;

        _harness.Settings.Current.PullStrategy.Should().Be(PullStrategy.Rebase);
        _harness.Settings.Current.AutoFetch.Should().BeFalse();
        _harness.Settings.Current.AutoFetchIntervalMinutes.Should().Be(30);
        _harness.Settings.Current.UseGitHubTokenForGit.Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(121.0)]
    [InlineData(2.5)]
    [InlineData(null)]
    public async Task Invalid_fetch_intervals_are_refused(double? minutes)
    {
        var git = Create();
        await git.ActivateAsync();

        git.AutoFetchInterval = minutes;

        git.HasAutoFetchIntervalError.Should().BeTrue();
        _harness.Settings.Current.AutoFetchIntervalMinutes.Should().Be(AppSettings.Default.AutoFetchIntervalMinutes);
    }

    [Fact]
    public async Task An_old_git_is_flagged()
    {
        _harness.Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(new GitInstallation("/usr/bin/git", "2.25.0"));
        var git = Create();

        await git.ActivateAsync();

        git.IsGitTooOld.Should().BeTrue();
        git.GitStatusTone.Should().Be(StatusTone.Warning);
    }

    [Fact]
    public async Task Identity_failures_are_reported()
    {
        _harness.Git.SetGlobalIdentityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForgeException(ErrorKind.GitCommandFailed, "could not lock config file")));
        var git = Create();
        await git.ActivateAsync();

        git.AuthorEmail = "ada@lovelace.dev";

        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Title == "Could not save your Git identity"), Arg.Any<NotificationAction?>());
    }

    private GitSettingsViewModel Create() =>
        new(_harness.CreateStore(), _harness.Git, _harness.Dialogs, _harness.Notifications, _harness.Shell, NullLogger.Instance);
}
