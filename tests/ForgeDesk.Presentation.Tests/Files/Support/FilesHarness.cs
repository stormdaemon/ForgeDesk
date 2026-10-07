using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Files.Support;

/// <summary>
/// A Files tab over fakes: an in-memory file system (<see cref="FakeFileService"/>), substitutes
/// for the index, content search and every workspace service, a real <see cref="ProjectContext"/>
/// whose git status is <see cref="Status"/>, and the <see cref="ImmediateDispatcher"/>.
/// </summary>
public sealed class FilesHarness : IDisposable
{
    private readonly List<FilesSectionViewModel> _sections = [];

    public FilesHarness()
    {
        Folder = new TestFolder();
        Project = TestData.Project("forge-app", Folder.Path);
        Git.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Status is { } status
            ? Task.FromResult(status)
            : Task.FromException<GitStatus>(new ForgeException(ErrorKind.NotARepository, "This folder is not a Git repository.")));
        Settings.Current.Returns(_ => AppSettings.Default);
        Services = new WorkspaceServices(Substitute.For<IWorkspaceSectionFactory>(), Git, Registry, Substitute.For<IProjectStatusService>(),
            Substitute.For<IRunService>(), Substitute.For<IWorkItemService>(), Substitute.For<IActivityLog>(), Settings, Dialogs, Notifications, Shell,
            Navigation, Substitute.For<IProjectActions>(), ImmediateDispatcher.Instance);
        Context = new ProjectContext(Project, Git, Substitute.For<IProjectDetector>(), Registry, ImmediateDispatcher.Instance);
        Context.NavigationRequested += (_, request) => NavigationRequests.Add(request);
    }

    public TestFolder Folder { get; }

    public Project Project { get; }

    public FakeFileService Files { get; } = new();

    public IFileIndex Index { get; } = Substitute.For<IFileIndex>();

    public IContentSearchService Search { get; } = Substitute.For<IContentSearchService>();

    public IGitService Git { get; } = Substitute.For<IGitService>();

    public IProjectRegistry Registry { get; } = Substitute.For<IProjectRegistry>();

    public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();

    public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

    public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

    public IShellIntegration Shell { get; } = Substitute.For<IShellIntegration>();

    public INavigationService Navigation { get; } = Substitute.For<INavigationService>();

    public WorkspaceServices Services { get; }

    public ProjectContext Context { get; }

    public List<WorkspaceNavigationRequest> NavigationRequests { get; } = [];

    /// <summary>What <c>git status</c> returns; null simulates a folder that is not a repository.</summary>
    public GitStatus? Status { get; set; } = TestData.Status();

    public string Root => Folder.Path;

    public FilesSectionViewModel Create()
    {
        var section = new FilesSectionViewModel(Context, Services, Files, Index, Search)
        {
            ChangeDebounce = TimeSpan.FromMilliseconds(10),
            GoToDelay = TimeSpan.Zero,
        };
        _sections.Add(section);
        return section;
    }

    public async Task<FilesSectionViewModel> OpenAsync()
    {
        await Context.RefreshGitStatusAsync();
        var section = Create();
        await section.ActivateAsync();
        return section;
    }

    public Task SetStatusAsync(GitStatus? status)
    {
        Status = status;
        return Context.RefreshGitStatusAsync();
    }

    public static FileEntry Dir(string path, bool heavy = false, bool hidden = false) => new()
    {
        Name = FileLocation.NameOf(path),
        RelativePath = path,
        IsDirectory = true,
        IsHeavyFolder = heavy,
        IsHidden = hidden || FileLocation.NameOf(path).StartsWith('.'),
    };

    public static FileEntry File(string path, long size = 100, bool hidden = false) => new()
    {
        Name = FileLocation.NameOf(path),
        RelativePath = path,
        Size = size,
        IsHidden = hidden || FileLocation.NameOf(path).StartsWith('.'),
        LastModified = TestData.Now.AddHours(-1),
    };

    public static string[] Paths(IEnumerable<FileTreeNodeViewModel> rows) => rows.Select(r => r.RelativePath).ToArray();

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

/// <summary>An in-memory project tree: folder listings and file contents, with call counts and gates.</summary>
public sealed class FakeFileService : IFileService
{
    private readonly Dictionary<string, List<FileEntry>> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<CancellationToken, Task<FileContent>>> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _listErrors = new(StringComparer.Ordinal);

    public Dictionary<string, int> ListCalls { get; } = new(StringComparer.Ordinal);

    public List<string> ReadCalls { get; } = [];

    public void SetFolder(string relativeFolder, params FileEntry[] entries) => _folders[relativeFolder] = [.. entries];

    public void FailListing(string relativeFolder, Exception error) => _listErrors[relativeFolder] = error;

    public void ClearFailure(string relativeFolder) => _listErrors.Remove(relativeFolder);

    public void SetText(string relativePath, string text, bool truncated = false, long? size = null) =>
        _reads[relativePath] = _ => Task.FromResult(new FileContent
        {
            RelativePath = relativePath,
            Kind = FileContentKind.Text,
            Text = text,
            Size = size ?? text.Length,
            IsTruncated = truncated,
            EncodingName = "UTF-8",
            LineEndings = "LF",
            LineCount = text.Split('\n').Length,
            LastModified = TestData.Now.AddHours(-2),
        });

    public void SetContent(string relativePath, FileContentKind kind, long size = 2048) =>
        _reads[relativePath] = _ => Task.FromResult(new FileContent { RelativePath = relativePath, Kind = kind, Size = size, LastModified = TestData.Now });

    public void SetRead(string relativePath, Func<CancellationToken, Task<FileContent>> read) => _reads[relativePath] = read;

    public void FailRead(string relativePath, Exception error) => _reads[relativePath] = _ => Task.FromException<FileContent>(error);

    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(string projectRoot, string relativeDirectory, CancellationToken cancellationToken = default)
    {
        ListCalls[relativeDirectory] = ListCalls.GetValueOrDefault(relativeDirectory) + 1;
        if (_listErrors.TryGetValue(relativeDirectory, out var error))
        {
            return Task.FromException<IReadOnlyList<FileEntry>>(error);
        }

        return _folders.TryGetValue(relativeDirectory, out var entries)
            ? Task.FromResult<IReadOnlyList<FileEntry>>(entries.ToList())
            : Task.FromException<IReadOnlyList<FileEntry>>(new ForgeException(ErrorKind.PathNotFound, $"The folder '{relativeDirectory}' does not exist."));
    }

    public Task<FileContent> ReadAsync(string projectRoot, string relativePath, long maxTextBytes = 4 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ReadCalls.Add(relativePath);
        return _reads.TryGetValue(relativePath, out var read)
            ? read(cancellationToken)
            : Task.FromException<FileContent>(new ForgeException(ErrorKind.PathNotFound, $"The file '{relativePath}' does not exist."));
    }

    public bool IsHeavyFolderName(string name) => name is "node_modules" or "bin" or "obj" or ".git";
}
