using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;

namespace ForgeDesk.Presentation.Settings;

/// <summary>Palette entries for each Settings section ("Settings: Appearance"…) and "Check for updates".</summary>
public sealed class SettingsPaletteSource : IPaletteSource
{
    private static readonly (string Key, string Title, string Icon, string Keywords)[] Entries =
    [
        ("General", "General", "Settings20", "clone folder startup restore close confirm onboarding welcome"),
        ("Appearance", "Appearance", "PaintBrush20", "theme dark light system accent color font size"),
        ("Git", "Git", "BranchFork20", "git executable path identity author name email pull rebase merge fetch"),
        ("GitHub", "GitHub", "Globe20", "github account sign in sign out token login rate limit"),
        ("Terminal", "Terminal", "WindowConsole20", "terminal shell powershell cmd bash wsl font"),
        ("Commands", "Commands", "Play20", "commands runs notify notification history stalled"),
        ("Editor", "Editor", "Code20", "editor code vscode visual studio sublime"),
        ("Updates", "Updates", "ArrowSync20", "update upgrade version release"),
        ("Data", "Data & storage", "Database20", "data storage folder database backup logs activity clear reset"),
        ("About", "About", "Info20", "about version license credits open source"),
    ];

    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly UpdateCoordinator _updates;

    public SettingsPaletteSource(INavigationService navigation, IPageFactory pages, UpdateCoordinator updates)
    {
        _navigation = navigation;
        _pages = pages;
        _updates = updates;
    }

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!_pages.IsAvailable(PageKind.Settings))
        {
            return Task.FromResult<IReadOnlyList<PaletteItem>>([]);
        }

        var items = new List<PaletteItem>();
        if (query.Wants(PaletteCategory.Setting))
        {
            foreach (var (key, title, icon, keywords) in Entries)
            {
                items.Add(new PaletteItem
                {
                    Title = $"Settings: {title}",
                    Icon = icon,
                    Category = PaletteCategory.Setting,
                    Keywords = "settings preferences options " + keywords,
                    Execute = () =>
                    {
                        _navigation.OpenSettings(key);
                        return Task.CompletedTask;
                    },
                });
            }
        }

        if (query.Wants(PaletteCategory.Action) && _updates.IsSupported)
        {
            items.Add(new PaletteItem
            {
                Title = _updates.HasUpdate ? $"Install ForgeDesk {_updates.AvailableVersion}" : "Check for updates",
                Subtitle = $"ForgeDesk {_updates.CurrentVersion}",
                Icon = "ArrowSync20",
                Category = PaletteCategory.Action,
                Keywords = "update upgrade version release new download restart",
                Execute = () =>
                {
                    _navigation.OpenSettings("Updates");
                    return _updates.HasUpdate ? _updates.DownloadAndRestartAsync() : _updates.CheckAsync();
                },
            });
        }

        return Task.FromResult<IReadOnlyList<PaletteItem>>(items);
    }
}
