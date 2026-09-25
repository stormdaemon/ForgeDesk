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

public sealed record PaletteQuery(string Text, string? CurrentProjectId);

/// <summary>Contributes items to the palette. Implementations must be fast and never throw.</summary>
public interface IPaletteSource
{
    Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken);
}
