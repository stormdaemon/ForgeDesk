using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Releases;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.GitHub.Support;

/// <summary>
/// The GitHub and Releases tabs over substitutes: a real <see cref="ProjectContext"/> on a temporary
/// folder linked to acme/forge-app, a signed-in GitHub service returning empty lists by default, and
/// the <see cref="ImmediateDispatcher"/>.
/// </summary>
public sealed class GitHubHarness : IDisposable
{
    private readonly List<IDisposable> _sections = [];

    public GitHubHarness(bool linked = true)
    {
        Folder = new TestFolder();
        Project = TestData.Project("forge-app", Folder.Path, gitHub: linked ? Repo : null);
        Git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Status is { } status
            ? Task.FromResult(status)
            : Task.FromException<GitStatus>(new ForgeException(ErrorKind.NotARepository, "This folder is not a Git repository.")));
        Git.GetRemotesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Remotes));
        Git.GetTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Tags));
        Git.GetBranchesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Branches));
        Registry.UpdateAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult(call.Arg<Project>()));
        Settings.Current.Returns(_ => CurrentSettings);
        Settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            CurrentSettings = call.Arg<Func<AppSettings, AppSettings>>()(CurrentSettings);
            Settings.Changed += Raise.Event<EventHandler<AppSettings>>(Settings, CurrentSettings);
            return Task.CompletedTask;
        });
        Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true);

        GitHub.IsSignedIn.Returns(_ => SignedIn);
        GitHub.GetRepositoryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(GitHubData.Repository()));
        GitHub.GetRateLimitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<RateLimitInfo?>(new RateLimitInfo(5000, 4812, TestData.Now.AddMinutes(40))));
        GitHub.GetPullRequestsAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<IssueStateFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubPullRequest>>([]));
        GitHub.GetIssuesAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<IssueStateFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubIssue>>([]));
        GitHub.GetIssueCommentsAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitHubComment>>([]));
        GitHub.GetCiSummaryAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CiSummary { State = CiState.Success, Branch = call.ArgAt<string?>(1) }));
        GitHub.GetWorkflowsAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<WorkflowInfo>>([]));
        GitHub.GetWorkflowRunsAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowRunInfo>>([]));
        GitHub.GetWorkflowJobsAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowJobInfo>>([]));
        GitHub.GetReleasesAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<GitHubRelease>>([]));
        GitHub.GetBranchesAsync(Arg.Any<GitHubRepoRef>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(["main"]));

        Services = new WorkspaceServices(Substitute.For<IWorkspaceSectionFactory>(), Git, Registry, Substitute.For<IProjectStatusService>(),
            Substitute.For<IRunService>(), WorkItems, Activity, Settings, Dialogs, Notifications, Shell, Navigation, Substitute.For<IProjectActions>(),
            ImmediateDispatcher.Instance)
        {
            InitialAutoFetchDelay = TimeSpan.FromHours(1),
        };
        Context = new ProjectContext(Project, Git, Substitute.For<IProjectDetector>(), Registry, ImmediateDispatcher.Instance);
    }

    public static GitHubRepoRef Repo { get; } = new("acme", "forge-app");

    public TestFolder Folder { get; }

    public Project Project { get; }

    public IGitService Git { get; } = Substitute.For<IGitService>();

    public IGitHubService GitHub { get; } = Substitute.For<IGitHubService>();

    public IGitHubAccountService Accounts { get; } = Substitute.For<IGitHubAccountService>();

    public IReleaseService Releases { get; } = Substitute.For<IReleaseService>();

    public IProjectRegistry Registry { get; } = Substitute.For<IProjectRegistry>();

    public IWorkItemService WorkItems { get; } = Substitute.For<IWorkItemService>();

    public IActivityLog Activity { get; } = Substitute.For<IActivityLog>();

    public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();

    public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

    public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

    public IShellIntegration Shell { get; } = Substitute.For<IShellIntegration>();

    public INavigationService Navigation { get; } = Substitute.For<INavigationService>();

    public WorkspaceServices Services { get; }

    public ProjectContext Context { get; }

    public AppSettings CurrentSettings { get; set; } = AppSettings.Default;

    public bool SignedIn { get; set; } = true;

    /// <summary>What <c>git status</c> returns; null simulates a folder that is not a repository.</summary>
    public GitStatus? Status { get; set; } = TestData.Status("main");

    public IReadOnlyList<GitRemote> Remotes { get; set; } = [new GitRemote("origin", "https://github.com/acme/forge-app.git", null)];

    public IReadOnlyList<GitTag> Tags { get; set; } = [];

    public IReadOnlyList<GitBranch> Branches { get; set; } = [];

    public string Root => Folder.Path;

    public async Task<GitHubSectionViewModel> OpenGitHubAsync(bool activate = true)
    {
        await Context.RefreshGitStatusAsync();
        var section = new GitHubSectionViewModel(Context, Services, GitHub, Accounts);
        _sections.Add(section);
        if (activate)
        {
            await section.ActivateAsync();
        }

        return section;
    }

    public async Task<ReleasesViewModel> OpenReleasesAsync(bool activate = true)
    {
        await Context.RefreshGitStatusAsync();
        var section = new ReleasesViewModel(Context, Services, GitHub, Accounts, Releases);
        _sections.Add(section);
        if (activate)
        {
            await section.ActivateAsync();
        }

        return section;
    }

    /// <summary>Changes the status and lets the context publish it, as the file watcher would.</summary>
    public Task SetStatusAsync(GitStatus? status)
    {
        Status = status;
        return Context.RefreshGitStatusAsync();
    }

    /// <summary>Makes the next "New …" dialog close with <paramref name="result"/> after <paramref name="fill"/> filled it.</summary>
    public void AnswerDialog<T>(Action<T> fill, bool? result = true)
        where T : class, IDialogViewModel =>
        Dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(call =>
        {
            if (call.Arg<IDialogViewModel>() is T dialog)
            {
                fill(dialog);
            }

            return Task.FromResult(result);
        });

    public void Dispose()
    {
        foreach (var section in _sections)
        {
            section.Dispose();
        }

        Context.Dispose();
        Folder.Dispose();
    }
}
