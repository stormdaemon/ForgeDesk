using ForgeDesk.Core.Detection;

namespace ForgeDesk.Presentation.Commands;

/// <summary>Display order, names and icons of command categories.</summary>
public static class CommandCategories
{
    /// <summary>Categories in the order the Commands tab lists them: what developers run most first.</summary>
    public static IReadOnlyList<CommandCategory> Ordered { get; } =
    [
        CommandCategory.Dev,
        CommandCategory.Build,
        CommandCategory.Test,
        CommandCategory.Lint,
        CommandCategory.Format,
        CommandCategory.Package,
        CommandCategory.Install,
        CommandCategory.Run,
        CommandCategory.Deploy,
        CommandCategory.Clean,
        CommandCategory.Other,
    ];

    /// <summary>Every category as a choice for the "Add command" form.</summary>
    public static IReadOnlyList<CommandCategoryOption> Options { get; } =
        Ordered.Select(c => new CommandCategoryOption(c, Name(c), Icon(c))).ToArray();

    public static int Rank(CommandCategory category)
    {
        for (var i = 0; i < Ordered.Count; i++)
        {
            if (Ordered[i] == category)
            {
                return i;
            }
        }

        return Ordered.Count;
    }

    public static string Name(CommandCategory category) => category switch
    {
        CommandCategory.Dev => "Dev",
        CommandCategory.Build => "Build",
        CommandCategory.Test => "Test",
        CommandCategory.Lint => "Lint",
        CommandCategory.Format => "Format",
        CommandCategory.Package => "Package",
        CommandCategory.Install => "Install",
        CommandCategory.Run => "Run",
        CommandCategory.Deploy => "Deploy",
        CommandCategory.Clean => "Clean",
        _ => "Other",
    };

    /// <summary>WPF-UI SymbolRegular name (16 px).</summary>
    public static string Icon(CommandCategory category) => category switch
    {
        CommandCategory.Dev => "Play16",
        CommandCategory.Build => "Wrench16",
        CommandCategory.Test => "Beaker16",
        CommandCategory.Lint => "CheckmarkCircle16",
        CommandCategory.Format => "TextAlignLeft16",
        CommandCategory.Package => "Box16",
        CommandCategory.Install => "ArrowDownload16",
        CommandCategory.Run => "Rocket16",
        CommandCategory.Deploy => "CloudArrowUp16",
        CommandCategory.Clean => "Broom16",
        _ => "WindowConsole20",
    };

    /// <summary>Parses "build", "Build"… (navigation arguments, palette keywords).</summary>
    public static CommandCategory? TryParse(string? text) =>
        Enum.TryParse<CommandCategory>(text?.Trim(), ignoreCase: true, out var category) && Enum.IsDefined(category) ? category : null;
}

/// <summary>A category choice of the command editor's ComboBox.</summary>
public sealed record CommandCategoryOption(CommandCategory Category, string Name, string Icon)
{
    public override string ToString() => Name;
}
