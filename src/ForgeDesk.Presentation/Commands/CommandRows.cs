using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Commands;

/// <summary>An entry of the Commands list: a category header or a command.</summary>
public abstract class CommandListEntry : ObservableObject
{
    public abstract bool IsHeader { get; }

    public abstract CommandCategory Category { get; }
}

/// <summary>Caption of a category ("BUILD 3").</summary>
public sealed partial class CommandGroupHeader : CommandListEntry
{
    public CommandGroupHeader(CommandCategory category)
    {
        Category = category;
    }

    public override bool IsHeader => true;

    public override CommandCategory Category { get; }

    public string Title => CommandCategories.Name(Category);

    public string Icon => CommandCategories.Icon(Category);

    [ObservableProperty]
    public partial int Count { get; internal set; }

    /// <summary>UI automation id ("Commands.Group.Build").</summary>
    public string AutomationId => $"Commands.Group.{Category}";

    public override string ToString() => Title;
}

/// <summary>A runnable command (detected from the project's files or user-defined).</summary>
public sealed partial class CommandRowViewModel : CommandListEntry
{
    public CommandRowViewModel(DetectedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Command = command;
    }

    public override bool IsHeader => false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Name), nameof(CommandLine), nameof(Category), nameof(Source), nameof(WorkingDirectory),
        nameof(HasWorkingDirectory), nameof(IsCustom), nameof(ToolTip), nameof(Description), nameof(HasDescription))]
    public partial DetectedCommand Command { get; internal set; }

    public string Id => Command.Id;

    public string Name => Command.Name;

    public string CommandLine => Command.CommandLine;

    public override CommandCategory Category => Command.Category;

    /// <summary>"package.json", "Cargo.toml", "custom"…</summary>
    public string Source => Command.Source;

    /// <summary>Directory relative to the project root, empty for the root.</summary>
    public string WorkingDirectory => Command.WorkingDirectory;

    public bool HasWorkingDirectory => !string.IsNullOrEmpty(Command.WorkingDirectory);

    public bool IsCustom => Command.IsCustom;

    public string? Description => Command.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Command.Description);

    public string ToolTip => Command.CommandLine
        + (HasWorkingDirectory ? $"\nin {Command.WorkingDirectory}" : string.Empty)
        + (HasDescription ? $"\n{Command.Description}" : string.Empty);

    /// <summary>Id of the run of this command in progress, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(RunButtonText), nameof(RunButtonIcon), nameof(RunButtonToolTip), nameof(LastResultTone),
        nameof(LastResultText))]
    public partial string? RunningRunId { get; internal set; }

    public bool IsRunning => RunningRunId is not null;

    /// <summary>Status of the latest finished run of this command (null: never run).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastResultTone), nameof(LastResultText), nameof(HasLastResult))]
    public partial RunStatus? LastStatus { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastResultText))]
    public partial DateTimeOffset? LastRunAt { get; internal set; }

    /// <summary>The start is being requested (button disabled meanwhile).</summary>
    [ObservableProperty]
    public partial bool IsStarting { get; internal set; }

    public bool HasLastResult => LastStatus is not null || IsRunning;

    public StatusTone LastResultTone => IsRunning ? StatusTone.Running : LastStatus switch
    {
        RunStatus.Succeeded => StatusTone.Success,
        RunStatus.Failed => StatusTone.Danger,
        RunStatus.Interrupted => StatusTone.Warning,
        RunStatus.Cancelled => StatusTone.Neutral,
        _ => StatusTone.None,
    };

    public string LastResultText => IsRunning ? "Running now"
        : LastStatus is { } status ? $"Last run {RunItemViewModel.StatusName(status).ToLowerInvariant()} {Format.RelativeTime(LastRunAt)}"
        : "Never run";

    public string RunButtonText => IsRunning ? "Stop" : "Run";

    public string RunButtonIcon => IsRunning ? "Stop16" : "Play16";

    public string RunButtonToolTip => IsRunning ? $"Stop {Name}" : $"Run {Name} (Enter)";

    /// <summary>UI automation id of the row's run button.</summary>
    public string RunButtonAutomationId => $"Commands.Run.{Id}";

    public override string ToString() => Name;

    /// <summary>True when the command matches the search text (name, command line, source, category).</summary>
    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || CommandLine.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Source.Contains(term, StringComparison.OrdinalIgnoreCase)
            || WorkingDirectory.Contains(term, StringComparison.OrdinalIgnoreCase)
            || CommandCategories.Name(Category).Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
