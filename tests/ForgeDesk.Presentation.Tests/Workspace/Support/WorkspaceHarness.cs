using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Workspace.Support;

/// <summary>
/// Substitutes for every service a project workspace uses, a real <see cref="ProjectContext"/> on a
/// real (temporary) folder, and the <see cref="ImmediateDispatcher"/>.
/// </summary>
public sealed class WorkspaceHarness : IDisposable
{
    private readonly List<ProjectWorkspaceViewModel> _workspaces = [];

    public WorkspaceHarness(params WorkspaceSection[] sections)
    {
        Folder = new TestFolder();
        Sections = new FakeSectionFactory(sections.Length == 0 ? Enum.GetValues<WorkspaceSection>() : sections);
        Settings.Current.Returns(_ => CurrentSettings);
        Runs.ActiveRuns.Returns(_ => ActiveRuns);
        Detector.DetectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Profile);
        Registry.GetCachedProfileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => (ProjectProfile?)Profile);
        Git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => CurrentStatus is { } status
            ? Task.FromResult(status)
            : Task.FromException<GitStatus>(new ForgeException(ErrorKind.NotARepository, "This folder is not a Git repository.")));
        Project = TestData.Project("forge-app", Folder.Path);
        Services = new WorkspaceServices(Sections, Git, Registry, Status, Runs, WorkItems, Activity, Settings, Dialogs, Notifications, Shell,
            Navigation, ProjectActions, ImmediateDispatcher.Instance)
        {
            // The periodic fetch never fires during a test; tests call AutoFetchOnceAsync directly.
            InitialAutoFetchDelay = TimeSpan.FromHours(1),
        };
    }

    public TestFolder Folder { get; }

    public Project Project { get; set; }

    public FakeSectionFactory Sections { get; }

    public IGitService Git { get; } = Substitute.For<IGitService>();

    public IProjectDetector Detector { get; } = Substitute.For<IProjectDetector>();

    public IProjectRegistry Registry { get; } = Substitute.For<IProjectRegistry>();

    public IProjectStatusService Status { get; } = Substitute.For<IProjectStatusService>();

    public IRunService Runs { get; } = Substitute.For<IRunService>();

    public IWorkItemService WorkItems { get; } = Substitute.For<IWorkItemService>();

    public IActivityLog Activity { get; } = Substitute.For<IActivityLog>();

    public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();

    public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

    public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

    public IShellIntegration Shell { get; } = Substitute.For<IShellIntegration>();

    public INavigationService Navigation { get; } = Substitute.For<INavigationService>();

    public IProjectActions ProjectActions { get; } = Substitute.For<IProjectActions>();

    public WorkspaceServices Services { get; }

    public AppSettings CurrentSettings { get; set; } = AppSettings.Default;

    public IReadOnlyList<IRunSession> ActiveRuns { get; set; } = [];

    /// <summary>What <c>git status</c> returns; null simulates a folder that is not a repository.</summary>
    public GitStatus? CurrentStatus { get; set; } = TestData.Status();

    public ProjectProfile Profile { get; set; } = new();

    public ProjectContext CreateContext(Project? project = null) =>
        new(project ?? Project, Git, Detector, Registry, ImmediateDispatcher.Instance);

    public ProjectWorkspaceViewModel CreateWorkspace(Project? project = null)
    {
        var workspace = new ProjectWorkspaceViewModel(CreateContext(project), Services);
        _workspaces.Add(workspace);
        return workspace;
    }

    /// <summary>A workspace that was navigated to (initialized, first tab shown).</summary>
    public async Task<ProjectWorkspaceViewModel> OpenWorkspaceAsync(Project? project = null)
    {
        var workspace = CreateWorkspace(project);
        await workspace.OnNavigatedToAsync(null);
        return workspace;
    }

    public static IRunSession Run(string projectId, string label, RunStatus status = RunStatus.Running, DateTimeOffset? startedAt = null)
    {
        var session = Substitute.For<IRunSession>();
        session.Request.Returns(new RunRequest { ProjectId = projectId, Label = label, CommandLine = label, WorkingDirectory = "." });
        session.Status.Returns(status);
        session.StartedAt.Returns(startedAt ?? TestData.Now);
        return session;
    }

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
        {
            workspace.Dispose();
        }

        Folder.Dispose();
    }
}
