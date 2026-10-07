using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git.Support;

/// <summary>
/// A Git tab over substitutes: a real <see cref="ProjectContext"/> on a temporary folder whose git
/// status is <see cref="Status"/>, every service faked, and the <see cref="ImmediateDispatcher"/>.
/// </summary>
public sealed class GitHarness : IDisposable
{
    private readonly List<GitSectionViewModel> _sections = [];

    public GitHarness()
    {
        Folder = new TestFolder();
        Project = TestData.Project("forge-app", Folder.Path);
        Git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => StatusError is { } error
            ? Task.FromException<GitStatus>(error)
            : Status is { } status
                ? Task.FromResult(status)
                : Task.FromException<GitStatus>(new ForgeException(ErrorKind.NotARepository, "This folder is not a Git repository.")));
        Git.GetLogAsync(Arg.Any<string>(), Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<GitCommit>>([]));
        Git.GetBranchesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<GitBranch>>([]));
        Git.GetMergedBranchesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        Git.GetDefaultBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("main"));
        Git.GetTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<GitTag>>([]));
        Git.GetStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<GitStash>>([]));
        Git.GetRemotesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<GitRemote>>([new GitRemote("origin", "https://github.com/acme/forge-app.git", null)]));
        Git.GetIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new GitIdentity("Ada Lovelace", "ada@example.com")));
        Git.GetFileDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiffTarget>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new FileDiff { Path = call.ArgAt<string>(1) }));
        Git.GetCommitDetailsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new GitCommitDetails(GitData.Commit(call.ArgAt<string>(1), "Details"), [])));
        Git.CommitAsync(Arg.Any<string>(), Arg.Any<GitCommitOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(GitData.Commit("abcdef1234567890", call.ArgAt<GitCommitOptions?>(1)?.Message.Split('\n')[0] ?? string.Empty)));
        Settings.Current.Returns(_ => AppSettings.Default);

        Services = new WorkspaceServices(Substitute.For<IWorkspaceSectionFactory>(), Git, Registry, Substitute.For<IProjectStatusService>(),
            Substitute.For<IRunService>(), Substitute.For<IWorkItemService>(), Activity, Settings, Dialogs, Notifications, Shell,
            Navigation, Substitute.For<IProjectActions>(), ImmediateDispatcher.Instance)
        {
            InitialAutoFetchDelay = TimeSpan.FromHours(1),
        };
        Context = new ProjectContext(Project, Git, Substitute.For<IProjectDetector>(), Registry, ImmediateDispatcher.Instance);
    }

    public TestFolder Folder { get; }

    public Project Project { get; }

    public IGitService Git { get; } = Substitute.For<IGitService>();

    public IProjectRegistry Registry { get; } = Substitute.For<IProjectRegistry>();

    public IActivityLog Activity { get; } = Substitute.For<IActivityLog>();

    public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();

    public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

    public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

    public IShellIntegration Shell { get; } = Substitute.For<IShellIntegration>();

    public INavigationService Navigation { get; } = Substitute.For<INavigationService>();

    public WorkspaceServices Services { get; }

    public ProjectContext Context { get; }

    public GitViewPreferences Preferences { get; } = new();

    /// <summary>What <c>git status</c> returns; null simulates a folder that is not a repository.</summary>
    public GitStatus? Status { get; set; } = GitData.Status();

    /// <summary>When set, <c>git status</c> fails with it (git missing, repository locked…).</summary>
    public Exception? StatusError { get; set; }

    public string Root => Folder.Path;

    /// <summary>A Git tab whose status was read, shown on <paramref name="view"/> and activated.</summary>
    public async Task<GitSectionViewModel> OpenAsync(GitView view = GitView.Changes, bool activate = true)
    {
        await Context.RefreshGitStatusAsync();
        Preferences.LastView = view;
        var section = new GitSectionViewModel(Context, Services, Preferences);
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

    public void ConfirmAll(bool answer = true) =>
        Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(Task.FromResult(answer));

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
