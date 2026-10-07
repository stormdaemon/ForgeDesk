using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Commands;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Commands.Support;

/// <summary>A Commands tab on a real context with substituted services (runs, custom commands, dialogs…).</summary>
public sealed class CommandsHarness : IDisposable
{
    private readonly List<CommandsSectionViewModel> _sections = [];

    public CommandsHarness()
    {
        Workspace = new WorkspaceHarness();
        CustomCommands.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<DetectedCommand>>(Custom));
        Runs.FindActive(Arg.Any<string>()).Returns((IRunSession?)null);
        Runs.GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<RunRecord>>(History));
    }

    public WorkspaceHarness Workspace { get; }

    public IRunService Runs => Workspace.Runs;

    public ICustomCommandStore CustomCommands { get; } = Substitute.For<ICustomCommandStore>();

    public List<DetectedCommand> Custom { get; } = [];

    public List<RunRecord> History { get; } = [];

    public string ProjectId => Workspace.Project.Id;

    public string Root => Workspace.Folder.Path;

    public ProjectContext Context { get; private set; } = null!;

    /// <summary>A Commands tab whose context has <paramref name="detected"/> as its profile's commands, activated (loaded).</summary>
    public async Task<CommandsSectionViewModel> CreateAsync(params DetectedCommand[] detected)
    {
        Workspace.Profile = new ProjectProfile { Commands = detected };
        Context = Workspace.CreateContext();
        await Context.InitializeAsync();
        var section = new CommandsSectionViewModel(Context, Workspace.Services, CustomCommands)
        {
            LogBatchInterval = Timeout.InfiniteTimeSpan,
            TickInterval = Timeout.InfiniteTimeSpan,
        };
        _sections.Add(section);
        await section.ActivateAsync();
        return section;
    }

    public static DetectedCommand Command(string name, CommandCategory category, string? commandLine = null, string source = "package.json",
        string workingDirectory = "", bool custom = false) => new()
    {
        Id = custom ? $"custom:{name}" : $"npm:{name}",
        Name = name,
        CommandLine = commandLine ?? $"npm run {name}",
        Category = category,
        Source = custom ? "custom" : source,
        WorkingDirectory = workingDirectory,
        IsCustom = custom,
    };

    public IRunSession Session(string id, DetectedCommand? command = null, RunStatus status = RunStatus.Running, DateTimeOffset? startedAt = null,
        IReadOnlyList<RunLogLine>? lines = null)
    {
        var session = Substitute.For<IRunSession>();
        session.Id.Returns(id);
        session.Request.Returns(new RunRequest
        {
            ProjectId = ProjectId,
            Label = command?.Name ?? "build",
            CommandLine = command?.CommandLine ?? "npm run build",
            WorkingDirectory = Root,
            Category = command?.Category ?? CommandCategory.Build,
            CommandId = command?.Id,
        });
        session.Status.Returns(status);
        session.StartedAt.Returns(startedAt ?? DateTimeOffset.Now.AddSeconds(-5));
        session.LastOutputAt.Returns(DateTimeOffset.Now);
        session.GetLines(Arg.Any<long>()).Returns(lines ?? []);
        return session;
    }

    public RunRecord Record(string id, RunStatus status, DetectedCommand? command = null, DateTimeOffset? startedAt = null, int? exitCode = null,
        string? errorSummary = null)
    {
        var started = startedAt ?? DateTimeOffset.Now.AddMinutes(-10);
        return new RunRecord
        {
            Id = id,
            ProjectId = ProjectId,
            CommandId = command?.Id,
            Label = command?.Name ?? "build",
            CommandLine = command?.CommandLine ?? "npm run build",
            WorkingDirectory = Root,
            Category = command?.Category ?? CommandCategory.Build,
            Status = status,
            StartedAt = started,
            EndedAt = status is RunStatus.Running ? null : started.AddSeconds(42),
            ExitCode = exitCode,
            LogPath = Path.Combine(Root, $"{id}.log"),
            ErrorSummary = errorSummary,
        };
    }

    public static RunLogLine Line(long index, string text, bool isError = false) => new(index, DateTimeOffset.Now, isError, text);

    public void Dispose()
    {
        foreach (var section in _sections)
        {
            section.Dispose();
        }

        Workspace.Dispose();
    }
}
