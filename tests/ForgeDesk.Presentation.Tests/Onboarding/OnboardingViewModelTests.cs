using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Onboarding;
using ForgeDesk.Presentation.Tests.Settings.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Onboarding;

public sealed class OnboardingViewModelTests : IDisposable
{
    private static readonly GitHubAccount Account = new(new GitHubUser("octo", "Octo Cat", "https://avatars/octo", "https://github.com/octo"),
        ["repo", "workflow"], GitHubAuthMethod.GitCredentialManager);

    private readonly FakeSettingsService _settings = new();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IGitHubAccountService _accounts = Substitute.For<IGitHubAccountService>();
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IShellIntegration _shell = Substitute.For<IShellIntegration>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IProjectCloneService _clones = Substitute.For<IProjectCloneService>();
    private readonly TestFolder _folder = new();
    private OnboardingViewModel? _onboarding;

    public OnboardingViewModelTests()
    {
        _git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(new GitInstallation(@"C:\Program Files\Git\cmd\git.exe", "2.46.0"));
        _registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Project>());
    }

    public void Dispose()
    {
        _onboarding?.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task It_starts_on_the_welcome_step_with_progress_dots()
    {
        var onboarding = await StartAsync();

        onboarding.IsWelcomeStep.Should().BeTrue();
        onboarding.NextText.Should().Be("Get started");
        onboarding.CanGoBack.Should().BeFalse();
        onboarding.StepCaption.Should().Be("Step 1 of 5");
        onboarding.Steps.Select(s => s.IsCurrent).Should().Equal(true, false, false, false, false);
        onboarding.Highlights.Should().HaveCount(3);
    }

    [Fact]
    public async Task The_environment_step_detects_git_and_the_github_cli()
    {
        _accounts.IsGitHubCliAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var onboarding = await StartAsync();

        await onboarding.NextCommand.ExecuteAsync(null);

        onboarding.IsEnvironmentStep.Should().BeTrue();
        onboarding.IsGitFound.Should().BeTrue();
        onboarding.GitStatusText.Should().Be("Git 2.46.0 is ready.");
        onboarding.GitTone.Should().Be(StatusTone.Success);
        onboarding.ShowGitGuidance.Should().BeFalse();
        onboarding.IsGitHubCliAvailable.Should().BeTrue();
        onboarding.Steps[0].IsDone.Should().BeTrue();
        onboarding.Steps[1].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_git_shows_guidance_and_check_again_finds_it()
    {
        _git.FindGitAsync(Arg.Any<CancellationToken>()).Returns((GitInstallation?)null, new GitInstallation("/usr/bin/git", "2.45.1"));
        var onboarding = await StartAsync();

        await onboarding.NextCommand.ExecuteAsync(null);

        onboarding.ShowGitGuidance.Should().BeTrue();
        onboarding.GitTone.Should().Be(StatusTone.Danger);
        onboarding.DownloadGitCommand.Execute(null);
        _shell.Received(1).OpenUrl(OnboardingViewModel.DownloadGitUrl);

        await onboarding.CheckEnvironmentCommand.ExecuteAsync(null);

        onboarding.ShowGitGuidance.Should().BeFalse();
        onboarding.GitStatusText.Should().Be("Git 2.45.1 is ready.");
    }

    [Fact]
    public async Task An_old_git_is_flagged()
    {
        _git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(new GitInstallation("/usr/bin/git", "2.20.1"));
        var onboarding = await StartAsync();

        await onboarding.NextCommand.ExecuteAsync(null);

        onboarding.IsGitTooOld.Should().BeTrue();
        onboarding.ShowGitGuidance.Should().BeTrue();
        onboarding.GitTone.Should().Be(StatusTone.Warning);
    }

    [Fact]
    public async Task The_github_step_can_be_skipped_or_continued_after_signing_in()
    {
        _accounts.SignInWithGitCredentialManagerAsync(Arg.Any<CancellationToken>()).Returns(Account);
        var onboarding = await StartAsync();
        await onboarding.GoToAsync(OnboardingStep.GitHub);

        onboarding.NextText.Should().Be("Skip for now");

        await onboarding.SignIn.SignInWithBrowserCommand.ExecuteAsync(null);

        onboarding.SignIn.IsSignedIn.Should().BeTrue();
        onboarding.NextText.Should().Be("Continue");
    }

    [Fact]
    public async Task Adding_a_local_folder_moves_to_the_last_step_and_finishing_opens_it()
    {
        var folder = _folder.Combine("forge-app");
        Directory.CreateDirectory(folder);
        var project = TestData.Project("forge-app", folder);
        _dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(folder);
        _registry.AddAsync(folder, null, Arg.Any<CancellationToken>()).Returns(project);
        var onboarding = await StartAsync();
        await onboarding.GoToAsync(OnboardingStep.FirstProject);
        onboarding.NextText.Should().Be("Skip for now");

        await onboarding.AddLocalFolderCommand.ExecuteAsync(null);

        onboarding.IsReadyStep.Should().BeTrue();
        onboarding.AddedProject.Should().Be(project);
        onboarding.AddedProjectText.Should().Contain("forge-app");
        onboarding.NextText.Should().Be("Open ForgeDesk");

        await onboarding.NextCommand.ExecuteAsync(null);

        _settings.Current.OnboardingCompleted.Should().BeTrue();
        await _navigation.Received(1).OpenProjectAsync(project.Id, null, null);
        _navigation.DidNotReceive().GoToDashboard();
    }

    [Fact]
    public async Task A_folder_already_registered_is_reused()
    {
        var folder = _folder.Combine("known");
        Directory.CreateDirectory(folder);
        var project = TestData.Project("known", folder);
        _dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(folder);
        _registry.FindByPathAsync(folder, Arg.Any<CancellationToken>()).Returns(project);
        var onboarding = await StartAsync();
        await onboarding.GoToAsync(OnboardingStep.FirstProject);

        await onboarding.AddLocalFolderCommand.ExecuteAsync(null);

        onboarding.AddedProject.Should().Be(project);
        await _registry.DidNotReceiveWithAnyArgs().AddAsync(default!, default, default);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_added_shows_the_error_and_stays()
    {
        _dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns("/");
        _registry.AddAsync("/", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Project>(new ForgeException(ErrorKind.InvalidInput, "'/' is the root of a drive, which is too broad to be a project.")));
        var onboarding = await StartAsync();
        await onboarding.GoToAsync(OnboardingStep.FirstProject);

        await onboarding.AddLocalFolderCommand.ExecuteAsync(null);

        onboarding.IsFirstProjectStep.Should().BeTrue();
        onboarding.Error!.Message.Should().Contain("too broad");
        onboarding.AddedProject.Should().BeNull();
    }

    [Fact]
    public async Task Cloning_from_onboarding_keeps_the_flow_and_opens_the_clone_at_the_end()
    {
        var project = TestData.Project("repo", _folder.Combine("repo"));
        _clones.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>>(), Arg.Any<CancellationToken>()).Returns(project);
        _dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(async call =>
        {
            var dialog = (CloneRepositoryDialogViewModel)call.Arg<IDialogViewModel>();
            bool? result = null;
            dialog.CloseRequested += (_, r) => result = r;
            dialog.Url = "https://github.com/owner/repo.git";
            await dialog.CloneCommand.ExecuteAsync(null);
            return result;
        });
        var onboarding = await StartAsync();
        await onboarding.GoToAsync(OnboardingStep.FirstProject);

        await onboarding.CloneCommand.ExecuteAsync(null);

        onboarding.IsReadyStep.Should().BeTrue();
        onboarding.AddedProject.Should().Be(project);
        await _navigation.DidNotReceiveWithAnyArgs().OpenProjectAsync(default!, default, default);
    }

    [Fact]
    public async Task Skipping_setup_completes_onboarding_and_shows_the_dashboard()
    {
        var onboarding = await StartAsync();

        await onboarding.SkipSetupCommand.ExecuteAsync(null);

        _settings.Current.OnboardingCompleted.Should().BeTrue();
        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task Finishing_without_a_project_shows_the_dashboard()
    {
        var onboarding = await StartAsync();
        await onboarding.GoToAsync(OnboardingStep.Ready);

        onboarding.ShowSkipSetup.Should().BeFalse();
        onboarding.Tips.Select(t => t.Keys).Should().Contain(["Ctrl+K", "Ctrl+O", "F5"]);

        await onboarding.NextCommand.ExecuteAsync(null);

        _settings.Current.OnboardingCompleted.Should().BeTrue();
        _navigation.Received(1).GoToDashboard();
    }

    [Fact]
    public async Task Back_returns_to_the_previous_step_and_running_again_restarts()
    {
        var onboarding = await StartAsync();
        await onboarding.NextCommand.ExecuteAsync(null);
        await onboarding.NextCommand.ExecuteAsync(null);
        onboarding.IsGitHubStep.Should().BeTrue();

        await onboarding.BackCommand.ExecuteAsync(null);

        onboarding.IsEnvironmentStep.Should().BeTrue();

        await onboarding.OnNavigatedToAsync(null);

        onboarding.IsWelcomeStep.Should().BeTrue();
        onboarding.BackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task A_failure_to_save_still_lets_the_user_in()
    {
        var onboarding = await StartAsync();
        _settings.FailWith = new ForgeException(ErrorKind.StorageFailure, "The database is locked.");

        await onboarding.SkipSetupCommand.ExecuteAsync(null);

        _notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Message == "The database is locked."), Arg.Any<NotificationAction?>());
        _navigation.Received(1).GoToDashboard();
    }

    private async Task<OnboardingViewModel> StartAsync()
    {
        var clone = new CloneRepositoryFlow(Substitute.For<IServiceProvider>(), _dialogs, _navigation, _notifications, NullLogger<CloneRepositoryFlow>.Instance,
            () => new CloneRepositoryDialogViewModel(Substitute.For<IGitHubService>(), _accounts, _clones, _registry,
                new FakeSettingsService(AppSettings.Default with { DefaultCloneDirectory = _folder.Path }), _dialogs, _navigation, _shell,
                ImmediateDispatcher.Instance, NullLogger<CloneRepositoryDialogViewModel>.Instance));
        _onboarding = new OnboardingViewModel(_settings, _git, _accounts, _registry, _dialogs, clone, _navigation, _shell, _notifications,
            ImmediateDispatcher.Instance, NullLogger<OnboardingViewModel>.Instance);
        await _onboarding.OnNavigatedToAsync(null);
        return _onboarding;
    }
}
