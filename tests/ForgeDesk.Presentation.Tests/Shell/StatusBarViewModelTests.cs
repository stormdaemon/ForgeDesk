using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Shell;

public sealed class StatusBarViewModelTests : IDisposable
{
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IRunService _runs = Substitute.For<IRunService>();
    private readonly IGitHubAccountService _github = Substitute.For<IGitHubAccountService>();
    private readonly IProjectActions _actions = Substitute.For<IProjectActions>();
    private readonly BackgroundOperations _operations = new(ImmediateDispatcher.Instance);
    private IReadOnlyList<IRunSession> _activeRuns = [];
    private StatusBarViewModel? _statusBar;

    public StatusBarViewModelTests() => _runs.ActiveRuns.Returns(_ => _activeRuns);

    public void Dispose() => _statusBar?.Dispose();

    [Fact]
    public void Running_commands_show_a_count_and_the_latest_label()
    {
        var statusBar = Create();
        _activeRuns =
        [
            WorkspaceHarness.Run("p1", "npm run dev", startedAt: TestData.Now.AddMinutes(-5)),
            WorkspaceHarness.Run("p2", "cargo test", startedAt: TestData.Now),
            WorkspaceHarness.Run("p1", "npm run build", RunStatus.Failed, TestData.Now.AddMinutes(1)),
        ];

        _runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_runs, _activeRuns[1]);

        statusBar.RunningCount.Should().Be(2);
        statusBar.HasRunning.Should().BeTrue();
        statusBar.LatestRunLabel.Should().Be("cargo test");
        statusBar.RunningToolTip.Should().Be("cargo test\nnpm run dev");
    }

    [Fact]
    public async Task Clicking_running_commands_opens_the_commands_tab_of_that_project()
    {
        var statusBar = Create();
        _activeRuns = [WorkspaceHarness.Run("p2", "cargo test")];
        _runs.RunStarted += Raise.Event<EventHandler<IRunSession>>(_runs, _activeRuns[0]);

        await statusBar.OpenRunningCommandsCommand.ExecuteAsync(null);

        await _actions.Received(1).OpenAsync("p2", WorkspaceSection.Commands, null);
    }

    [Fact]
    public void Background_operations_show_the_first_one_and_how_many_more()
    {
        var statusBar = Create();

        using var clone = _operations.Begin("Cloning octo/forge");
        using var analysis = _operations.Begin("Analyzing forge-app");
        clone.Message = "Receiving objects";

        statusBar.CurrentOperation.Should().BeSameAs(clone);
        statusBar.MoreOperationsText.Should().Be("+1");
        statusBar.HasOperations.Should().BeTrue();
        statusBar.OperationsToolTip.Should().Contain("Cloning octo/forge — Receiving objects");
    }

    [Fact]
    public void GitHub_account_shows_login_or_sign_in()
    {
        var statusBar = Create();
        statusBar.GitHubText.Should().Be("Sign in");
        statusBar.IsSignedIn.Should().BeFalse();

        _github.AccountChanged += Raise.Event<EventHandler<GitHubAccount?>>(_github,
            new GitHubAccount(new GitHubUser("octocat", "Octo Cat", "https://avatars/octocat", "https://github.com/octocat"), [],
                GitHubAuthMethod.GitCredentialManager));

        statusBar.IsSignedIn.Should().BeTrue();
        statusBar.GitHubText.Should().Be("octocat");
        statusBar.GitHubAvatarUrl.Should().Be("https://avatars/octocat");

        statusBar.OpenGitHubAccountCommand.Execute(null);
        _navigation.Received(1).OpenSettings("GitHub");
    }

    [Fact]
    public async Task The_current_project_feeds_the_left_side()
    {
        using var harness = new WorkspaceHarness(WorkspaceSection.Overview, WorkspaceSection.Git);
        var workspace = await harness.OpenWorkspaceAsync();
        var statusBar = Create();

        _navigation.CurrentPage.Returns(workspace);
        _navigation.Navigated += Raise.Event();

        statusBar.Workspace.Should().BeSameAs(workspace);
        await statusBar.OpenGitChangesCommand.ExecuteAsync(null);
        workspace.CurrentSection!.Section.Should().Be(WorkspaceSection.Git);
    }

    [Fact]
    public void Update_availability_is_a_plain_property()
    {
        var statusBar = Create();
        statusBar.HasUpdate.Should().BeFalse();

        statusBar.UpdateAvailableVersion = "1.4.0";

        statusBar.HasUpdate.Should().BeTrue();
        statusBar.UpdateText.Should().Be("Update 1.4.0 available");
    }

    private StatusBarViewModel Create()
    {
        _statusBar = new StatusBarViewModel(_navigation, _runs, _operations, _github, _actions, ImmediateDispatcher.Instance);
        return _statusBar;
    }
}
