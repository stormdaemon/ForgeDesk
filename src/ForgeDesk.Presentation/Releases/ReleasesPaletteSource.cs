using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Releases;

/// <summary>"New release…" in the command palette for the project on screen when it is linked to GitHub.</summary>
public sealed class ReleasesPaletteSource : IPaletteSource
{
    private readonly INavigationService _navigation;

    public ReleasesPaletteSource(INavigationService navigation)
    {
        _navigation = navigation;
    }

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.Wants(PaletteCategory.Action)
            || _navigation.CurrentPage is not ProjectWorkspaceViewModel workspace
            || workspace.GitHub is not { } repo
            || !workspace.AvailableTabs.Any(t => t.Section == WorkspaceSection.Releases))
        {
            return Task.FromResult<IReadOnlyList<PaletteItem>>([]);
        }

        return Task.FromResult<IReadOnlyList<PaletteItem>>(
        [
            new PaletteItem
            {
                Title = "New release…",
                Subtitle = $"{workspace.Name} · {repo.FullName}",
                Icon = "Rocket20",
                Category = PaletteCategory.Action,
                Keywords = "release publish tag version changelog ship github",
                Boost = 1,
                Execute = () => workspace.SelectSectionAsync(WorkspaceSection.Releases, ReleasesNavigation.NewRelease()),
            },
        ]);
    }
}
