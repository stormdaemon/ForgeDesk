namespace ForgeDesk.Presentation.Palette;

/// <summary>Search scope selected by the first character of the palette query.</summary>
public enum PaletteMode
{
    All,

    /// <summary>"&gt;" — actions, navigation, settings and project commands.</summary>
    Actions,

    /// <summary>"@" — projects.</summary>
    Projects,

    /// <summary>"#" — tasks.</summary>
    Tasks,

    /// <summary>"/" — files of the current project.</summary>
    Files,
}

/// <summary>A prefix chip shown under the empty search box.</summary>
public sealed record PalettePrefixHint(string Prefix, string Label, PaletteMode Mode);

/// <summary>The palette query split into its mode (prefix) and search term.</summary>
public sealed record ParsedPaletteQuery(PaletteMode Mode, string Term, IReadOnlySet<PaletteCategory>? Categories)
{
    public bool IsEmpty => Term.Length == 0;
}

public static class PaletteQueryParser
{
    private static readonly IReadOnlySet<PaletteCategory> ActionCategories =
        new HashSet<PaletteCategory> { PaletteCategory.Action, PaletteCategory.Navigation, PaletteCategory.Setting, PaletteCategory.Command };

    private static readonly IReadOnlySet<PaletteCategory> ProjectCategories = new HashSet<PaletteCategory> { PaletteCategory.Project };
    private static readonly IReadOnlySet<PaletteCategory> TaskCategories = new HashSet<PaletteCategory> { PaletteCategory.Task };
    private static readonly IReadOnlySet<PaletteCategory> FileCategories = new HashSet<PaletteCategory> { PaletteCategory.File };

    public static IReadOnlyList<PalettePrefixHint> Hints { get; } =
    [
        new(">", "Actions", PaletteMode.Actions),
        new("@", "Projects", PaletteMode.Projects),
        new("#", "Tasks", PaletteMode.Tasks),
        new("/", "Files", PaletteMode.Files),
    ];

    public static ParsedPaletteQuery Parse(string? text)
    {
        var value = text?.TrimStart() ?? string.Empty;
        if (value.Length == 0)
        {
            return new ParsedPaletteQuery(PaletteMode.All, string.Empty, null);
        }

        var (mode, categories) = value[0] switch
        {
            '>' => (PaletteMode.Actions, ActionCategories),
            '@' => (PaletteMode.Projects, ProjectCategories),
            '#' => (PaletteMode.Tasks, TaskCategories),
            '/' => (PaletteMode.Files, FileCategories),
            _ => (PaletteMode.All, null),
        };

        var term = mode == PaletteMode.All ? value.Trim() : value[1..].Trim();
        return new ParsedPaletteQuery(mode, term, categories);
    }

    public static string Placeholder(PaletteMode mode) => mode switch
    {
        PaletteMode.Actions => "Run an action…",
        PaletteMode.Projects => "Open a project…",
        PaletteMode.Tasks => "Find a task…",
        PaletteMode.Files => "Go to a file in this project…",
        _ => "Search projects, actions, files and tasks",
    };

    public static string CategoryTitle(PaletteCategory category) => category switch
    {
        PaletteCategory.Action => "Actions",
        PaletteCategory.Project => "Projects",
        PaletteCategory.File => "Files",
        PaletteCategory.Branch => "Branches",
        PaletteCategory.Command => "Commands",
        PaletteCategory.Task => "Tasks",
        PaletteCategory.Navigation => "Go to",
        PaletteCategory.Setting => "Settings",
        _ => category.ToString(),
    };
}
