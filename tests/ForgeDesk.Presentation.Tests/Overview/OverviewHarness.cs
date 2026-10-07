using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Overview;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Overview;

/// <summary>
/// An Overview tab over substitutes: a real <see cref="ProjectContext"/> on a temporary folder whose
/// git status is <see cref="Status"/>, every service faked, and the <see cref="ImmediateDispatcher"/>.
/// </summary>
public sealed class OverviewHarness : IDisposable
{
    private readonly List<OverviewViewModel> _created = [];

    public OverviewHarness(GitHubRepoRef? gitHub = null)
    {
        Folder = new TestFolder();
        Project = TestData.Project("forge-app", Folder.Path, gitHub: gitHub);
        Git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Status is { } status
            ? Task.FromResult(status)
            : Task.FromException<GitStatus>(new ForgeException(ErrorKind.NotARepository, "This folder is not a Git repository.")));
        Git.GetLogAsync(Arg.Any<string>(), Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<GitCommit>>(Commits));
        StatusService.GetCachedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot));
        StatusService.RefreshAsync(Arg.Any<Project>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Snapshot ?? new ProjectSnapshot { ProjectId = Project.Id }));
        Runs.GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<RunRecord>>(History));
        Runs.ActiveRuns.Returns(_ => ActiveRuns);
        WorkItems.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<WorkItem>>(Tasks));
        Activity.QueryAsync(Arg.Any<ActivityQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<ActivityEntry>>(Entries));
        Files.ReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => Readme is { } text && call.ArgAt<string>(1) == "README.md"
                ? Task.FromResult(new FileContent { RelativePath = "README.md", Kind = FileContentKind.Text, Text = text })
                : Task.FromException<FileContent>(new ForgeException(ErrorKind.PathNotFound, "The file does not exist.")));
        GitHub.IsSignedIn.Returns(_ => SignedIn);

        Detector.DetectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Profile ?? new ProjectProfile()));
        Registry.GetCachedProfileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Profile));
        Context = new ProjectContext(Project, Git, Detector, Registry, ImmediateDispatcher.Instance);
        Context.NavigationRequested += (_, request) => Navigations.Add(request);
    }

    public TestFolder Folder { get; }

    public Project Project { get; }

    public ProjectContext Context { get; }

    public GitStatus? Status { get; set; }

    /// <summary>Profile returned by detection and the registry cache (see <see cref="ProjectContext.InitializeAsync"/>).</summary>
    public ProjectProfile? Profile { get; set; }

    public IProjectDetector Detector { get; } = Substitute.For<IProjectDetector>();

    public List<GitCommit> Commits { get; } = [];

    public ProjectSnapshot? Snapshot { get; set; }

    public List<RunRecord> History { get; } = [];

    public List<IRunSession> ActiveRuns { get; } = [];

    public List<WorkItem> Tasks { get; } = [];

    public List<ActivityEntry> Entries { get; } = [];

    public string? Readme { get; set; }

    public bool SignedIn { get; set; }

    public List<WorkspaceNavigationRequest> Navigations { get; } = [];

    public IGitService Git { get; } = Substitute.For<IGitService>();

    public IProjectRegistry Registry { get; } = Substitute.For<IProjectRegistry>();

    public IProjectStatusService StatusService { get; } = Substitute.For<IProjectStatusService>();

    public IGitHubService GitHub { get; } = Substitute.For<IGitHubService>();

    public IGitHubAccountService Accounts { get; } = Substitute.For<IGitHubAccountService>();

    public IRunService Runs { get; } = Substitute.For<IRunService>();

    public IWorkItemService WorkItems { get; } = Substitute.For<IWorkItemService>();

    public IActivityLog Activity { get; } = Substitute.For<IActivityLog>();

    public IFileService Files { get; } = Substitute.For<IFileService>();

    public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

    public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

    public IShellIntegration Shell { get; } = Substitute.For<IShellIntegration>();

    public INavigationService Navigation { get; } = Substitute.For<INavigationService>();

    public OverviewViewModel Create()
    {
        var overview = new OverviewViewModel(Context, Git, StatusService, GitHub, Accounts, Runs, WorkItems, Activity, Files, Registry,
            Dialogs, Notifications, Shell, Navigation, ImmediateDispatcher.Instance);
        _created.Add(overview);
        return overview;
    }

    /// <summary>Reads <see cref="Status"/> into the context (raising GitStatusChanged / RepositoryChanged).</summary>
    public Task RefreshStatusAsync() => Context.RefreshGitStatusAsync();

    public static GitCommit Commit(string sha, string subject, string author = "Ada Lovelace", DateTimeOffset? when = null) => new()
    {
        Sha = sha,
        Subject = subject,
        Author = new GitSignature(author, "ada@example.com", when ?? TestData.Now),
    };

    public static WorkItem Item(string id, int number, string title, WorkItemStatus status, WorkItemPriority priority = WorkItemPriority.None) => new()
    {
        Id = id,
        ProjectId = "p",
        Number = number,
        Title = title,
        Status = status,
        Priority = priority,
        SortOrder = number,
    };

    public void Dispose()
    {
        foreach (var overview in _created)
        {
            overview.Dispose();
        }

        Context.Dispose();
        Folder.Dispose();
    }
}
