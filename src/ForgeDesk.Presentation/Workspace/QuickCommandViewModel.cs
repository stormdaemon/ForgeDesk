using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Detection;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>A one-click Dev / Build / Test button in the workspace header.</summary>
public sealed partial class QuickCommandViewModel : ObservableObject
{
    public QuickCommandViewModel(DetectedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Command = command;
    }

    /// <summary>Categories that get a header button, in display order.</summary>
    public static IReadOnlyList<CommandCategory> Categories { get; } = [CommandCategory.Dev, CommandCategory.Build, CommandCategory.Test];

    public DetectedCommand Command { get; }

    public CommandCategory Category => Command.Category;

    public string Label => Category switch
    {
        CommandCategory.Dev => "Dev",
        CommandCategory.Build => "Build",
        CommandCategory.Test => "Test",
        _ => Command.Name,
    };

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon => Category switch
    {
        CommandCategory.Dev => "Play20",
        CommandCategory.Build => "Wrench20",
        CommandCategory.Test => "Beaker20",
        _ => "Play20",
    };

    public string ToolTip => $"Run {Command.Name}\n{Command.CommandLine}";

    [ObservableProperty]
    public partial bool IsStarting { get; set; }

    /// <summary>Picks the first detected command of each quick category.</summary>
    public static IReadOnlyList<DetectedCommand> Select(ProjectProfile? profile) =>
        profile is null
            ? []
            : Categories.Select(c => profile.CommandsIn(c).FirstOrDefault()).OfType<DetectedCommand>().ToArray();
}
