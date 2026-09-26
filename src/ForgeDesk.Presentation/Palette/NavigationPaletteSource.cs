using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Palette;

/// <summary>Go to Dashboard / Activity / Settings, back, and the tabs of the open project (with their Ctrl+digit shortcuts).</summary>
public sealed class NavigationPaletteSource : IPaletteSource
{
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;

    public NavigationPaletteSource(INavigationService navigation, IPageFactory pages)
    {
        _navigation = navigation;
        _pages = pages;
    }

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.Wants(PaletteCategory.Navigation))
        {
            return Task.FromResult<IReadOnlyList<PaletteItem>>([]);
        }

        var items = new List<PaletteItem>();
        AddPage(items, PageKind.Dashboard, "Go to Dashboard", "Home20", null, "home projects overview start", _navigation.GoToDashboard);
        AddPage(items, PageKind.Activity, "Go to Activity", "History20", null, "journal log timeline history", _navigation.OpenActivity);
        AddPage(items, PageKind.Settings, "Open Settings", "Settings20", "Ctrl+,", "preferences options configuration",
            () => _navigation.OpenSettings());

        if (_navigation.CanGoBack)
        {
            items.Add(new PaletteItem
            {
                Title = "Go back",
                Icon = "ArrowLeft20",
                Category = PaletteCategory.Navigation,
                Shortcut = "Alt+Left",
                Keywords = "previous page return",
                Execute = () =>
                {
                    _navigation.GoBack();
                    return Task.CompletedTask;
                },
            });
        }

        if (_navigation.CurrentPage is ProjectWorkspaceViewModel workspace)
        {
            foreach (var tab in workspace.AvailableTabs)
            {
                var section = tab.Section;
                items.Add(new PaletteItem
                {
                    Title = $"Go to {tab.Title}",
                    Subtitle = workspace.Name,
                    Icon = tab.Icon,
                    Category = PaletteCategory.Navigation,
                    Shortcut = tab.Shortcut,
                    Keywords = tab.Info.Keywords,
                    Boost = 1,
                    Execute = () => workspace.SelectSectionAsync(section),
                });
            }
        }

        return Task.FromResult<IReadOnlyList<PaletteItem>>(items);
    }

    private void AddPage(List<PaletteItem> items, PageKind kind, string title, string icon, string? shortcut, string keywords, Action navigate)
    {
        if (!_pages.IsAvailable(kind))
        {
            return;
        }

        items.Add(new PaletteItem
        {
            Title = title,
            Icon = icon,
            Category = PaletteCategory.Navigation,
            Shortcut = shortcut,
            Keywords = keywords,
            Execute = () =>
            {
                navigate();
                return Task.CompletedTask;
            },
        });
    }
}
