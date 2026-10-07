using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>A primary command of the project (Dev, Build, Test, Lint) with its last result.</summary>
public sealed partial class OverviewCommandViewModel : ObservableObject
{
    public OverviewCommandViewModel(DetectedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Command = command;
    }

    public DetectedCommand Command { get; }

    public string Id => Command.Id;

    public string Name => Command.Name;

    public string CommandLine => Command.CommandLine;

    public string CategoryLabel => Command.Category switch
    {
        CommandCategory.Dev => "Dev",
        CommandCategory.Build => "Build",
        CommandCategory.Test => "Test",
        CommandCategory.Lint => "Lint",
        _ => Command.Category.ToString(),
    };

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon => Command.Category switch
    {
        CommandCategory.Dev => "Play20",
        CommandCategory.Build => "Wrench20",
        CommandCategory.Test => "Beaker20",
        CommandCategory.Lint => "CheckmarkStarburst20",
        _ => "Play20",
    };

    public string ToolTip => string.IsNullOrEmpty(Command.WorkingDirectory)
        ? $"Run {Command.Name}\n{Command.CommandLine}"
        : $"Run {Command.Name} in {Command.WorkingDirectory}\n{Command.CommandLine}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsStarting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(LastRunText))]
    [NotifyPropertyChangedFor(nameof(LastTone))]
    public partial bool IsRunning { get; set; }

    public bool IsBusy => IsStarting || IsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastRun))]
    [NotifyPropertyChangedFor(nameof(LastRunText))]
    [NotifyPropertyChangedFor(nameof(LastTone))]
    public partial RunRecord? LastRun { get; set; }

    public bool HasLastRun => LastRun is not null || IsRunning;

    /// <summary>Id of the run to show in the Commands tab (running, else the last one).</summary>
    public string? RunningId { get; set; }

    public string LastRunText => IsRunning
        ? "Running…"
        : LastRun switch
        {
            null => "Never run",
            { Status: RunStatus.Succeeded } run => $"Succeeded in {Format.Duration(run.Duration)}",
            { Status: RunStatus.Failed } run => run.ExitCode is { } code ? $"Failed (exit code {code})" : "Failed",
            { Status: RunStatus.Cancelled } => "Cancelled",
            { Status: RunStatus.Interrupted } => "Interrupted",
            _ => "Running…",
        };

    public StatusTone LastTone => IsRunning
        ? StatusTone.Running
        : LastRun?.Status switch
        {
            RunStatus.Succeeded => StatusTone.Success,
            RunStatus.Failed => StatusTone.Danger,
            RunStatus.Interrupted => StatusTone.Warning,
            null => StatusTone.None,
            _ => StatusTone.Neutral,
        };
}

/// <summary>"Commands" card: one-click Dev / Build / Test / Lint with the last result of each.</summary>
public sealed partial class OverviewCommandsViewModel : OverviewCardViewModel
{
    internal static readonly CommandCategory[] Categories = [CommandCategory.Dev, CommandCategory.Build, CommandCategory.Test, CommandCategory.Lint];

    private readonly IRunService _runs;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;

    public OverviewCommandsViewModel(ProjectContext context, IRunService runs, INotificationService notifications, IUiDispatcher dispatcher)
        : base(context)
    {
        _runs = runs;
        _notifications = notifications;
        _dispatcher = dispatcher;
        Context.PropertyChanged += OnContextPropertyChanged;
        _runs.RunStarted += OnRunStarted;
        _runs.RunCompleted += OnRunCompleted;
        RebuildCommands();
    }

    public ObservableCollection<OverviewCommandViewModel> Commands { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoCommands))]
    public partial bool HasCommands { get; private set; }

    /// <summary>The project was analyzed and has no primary command.</summary>
    public bool ShowNoCommands => HasLoaded && Context.Profile is not null && !HasCommands;

    /// <summary>Total number of commands detected (the card shows the primary ones).</summary>
    [ObservableProperty]
    public partial int TotalCommands { get; private set; }

    public string AllCommandsText => TotalCommands > Commands.Count ? $"All {TotalCommands} commands" : "All commands";

    protected override string ErrorTitle => "Could not read the run history";

    /// <summary>The first detected command of each primary category.</summary>
    internal static IReadOnlyList<DetectedCommand> Select(ProjectProfile? profile) =>
        profile is null ? [] : Categories.Select(c => profile.CommandsIn(c).FirstOrDefault()).OfType<DetectedCommand>().ToArray();

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        RebuildCommands();
        if (Commands.Count == 0)
        {
            return;
        }

        var history = await _runs.GetHistoryAsync(Context.ProjectId, 200, cancellationToken).ConfigureAwait(true);
        foreach (var command in Commands)
        {
            command.LastRun = history
                .Where(r => string.Equals(r.CommandId, command.Id, StringComparison.Ordinal) && r.IsFinished)
                .MaxBy(r => r.StartedAt);
        }

        UpdateRunning();
    }

    [RelayCommand]
    private async Task StartAsync(OverviewCommandViewModel? command)
    {
        if (command is null || command.IsStarting)
        {
            return;
        }

        command.IsStarting = true;
        try
        {
            var definition = command.Command;
            var directory = string.IsNullOrEmpty(definition.WorkingDirectory)
                ? Context.Root
                : PathUtil.ResolveUnder(Context.Root, definition.WorkingDirectory);
            var session = await _runs.StartAsync(new RunRequest
            {
                ProjectId = Context.ProjectId,
                Label = definition.Name,
                CommandLine = definition.CommandLine,
                WorkingDirectory = directory,
                Category = definition.Category,
                CommandId = definition.Id,
            }, Context.Lifetime).ConfigureAwait(true);
            command.RunningId = session.Id;
            command.IsRunning = session.Status is RunStatus.Running or RunStatus.Queued;
            Context.RequestNavigation(WorkspaceSection.Commands, session.Id);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, $"Could not start {command.Name}"));
        }
        finally
        {
            command.IsStarting = false;
        }
    }

    /// <summary>Shows the running (or last) run of a command in the Commands tab.</summary>
    [RelayCommand]
    private void ShowRun(OverviewCommandViewModel? command)
    {
        if (command is null)
        {
            return;
        }

        var runId = command.IsRunning ? command.RunningId : command.LastRun?.Id;
        Context.RequestNavigation(WorkspaceSection.Commands, runId);
    }

    [RelayCommand]
    private void OpenCommands() => Context.RequestNavigation(WorkspaceSection.Commands);

    private void RebuildCommands()
    {
        var selected = Select(Context.Profile);
        var existing = Commands.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var desired = selected
            .Select(c => existing.TryGetValue(c.Id, out var current)
                ? current.Command == c ? current : new OverviewCommandViewModel(c) { LastRun = current.LastRun }
                : new OverviewCommandViewModel(c))
            .ToList();

        Commands.SyncWith(desired);
        HasCommands = Commands.Count > 0;
        TotalCommands = Context.Profile?.Commands.Count ?? 0;
        OnPropertyChanged(nameof(AllCommandsText));
        UpdateRunning();
    }

    protected override void OnLoadStateChanged() => OnPropertyChanged(nameof(ShowNoCommands));

    private void UpdateRunning()
    {
        foreach (var command in Commands)
        {
            var active = _runs.ActiveRuns
                .Where(r => string.Equals(r.Request.ProjectId, Context.ProjectId, StringComparison.Ordinal)
                    && string.Equals(r.Request.CommandId, command.Id, StringComparison.Ordinal)
                    && r.Status is RunStatus.Running or RunStatus.Queued)
                .MaxBy(r => r.StartedAt);
            command.IsRunning = active is not null;
            if (active is not null)
            {
                command.RunningId = active.Id;
            }
        }
    }

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectContext.Profile))
        {
            OnPropertyChanged(nameof(ShowNoCommands));
            var before = Commands.Select(c => c.Id).ToList();
            RebuildCommands();
            if (!before.SequenceEqual(Commands.Select(c => c.Id), StringComparer.Ordinal))
            {
                RequestReload();
            }
        }
    }

    private void OnRunStarted(object? sender, IRunSession session)
    {
        if (string.Equals(session.Request.ProjectId, Context.ProjectId, StringComparison.Ordinal))
        {
            _dispatcher.Post(() =>
            {
                if (!IsDisposed)
                {
                    UpdateRunning();
                }
            });
        }
    }

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e)
    {
        var record = e.Record;
        if (!string.Equals(record.ProjectId, Context.ProjectId, StringComparison.Ordinal))
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            if (Commands.FirstOrDefault(c => string.Equals(c.Id, record.CommandId, StringComparison.Ordinal)) is { } command
                && (command.LastRun is null || command.LastRun.StartedAt <= record.StartedAt))
            {
                command.LastRun = record;
            }

            UpdateRunning();
        });
    }

    protected override void OnDispose()
    {
        Context.PropertyChanged -= OnContextPropertyChanged;
        _runs.RunStarted -= OnRunStarted;
        _runs.RunCompleted -= OnRunCompleted;
    }
}
