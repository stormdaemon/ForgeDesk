namespace ForgeDesk.Presentation.Palette;

public enum PaletteCategory
{
    Action,
    Project,
    File,
    Branch,
    Command,
    Task,
    Navigation,
    Setting,
}

/// <summary>An entry of the command palette (Ctrl+K).</summary>
public sealed record PaletteItem
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }

    /// <summary>WPF-UI SymbolRegular name, e.g. "Branch24".</summary>
    public string Icon { get; init; } = "Search24";
    public PaletteCategory Category { get; init; }
    public string? Shortcut { get; init; }

    /// <summary>Extra words matched by the search (aliases like "sync" for "Pull").</summary>
    public string? Keywords { get; init; }

    /// <summary>Base ranking boost (recently used, current project…).</summary>
    public double Boost { get; init; }

    public required Func<Task> Execute { get; init; }
}

/// <summary>
/// What the user typed (without the mode prefix) and the project on screen. <see cref="Categories"/>
/// is set in prefixed modes ("@" projects, "#" tasks…): sources can skip work for other categories.
/// </summary>
public sealed record PaletteQuery(string Text, string? CurrentProjectId)
{
    /// <summary>Categories the palette will show; null means every category.</summary>
    public IReadOnlySet<PaletteCategory>? Categories { get; init; }

    public bool Wants(PaletteCategory category) => Categories is null || Categories.Contains(category);
}

/// <summary>Contributes items to the palette. Implementations must be fast and never throw.</summary>
public interface IPaletteSource
{
    Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken);
}
