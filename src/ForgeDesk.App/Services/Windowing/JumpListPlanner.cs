using ForgeDesk.Core.Projects;

namespace ForgeDesk.App.Services.Windowing;

/// <summary>A project shown in the taskbar jump list, under a category.</summary>
internal sealed record JumpListEntry(Project Project, string Category);

/// <summary>Chooses which projects appear in the jump list: pinned first, then the most recent.</summary>
internal static class JumpListPlanner
{
    public const string PinnedCategory = "Pinned projects";
    public const string RecentCategory = "Recent projects";
    public const int MaxPinned = 5;
    public const int MaxTotal = 10;

    public static IReadOnlyList<JumpListEntry> Plan(IEnumerable<Project> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var all = projects.ToList();
        var pinned = all
            .Where(p => p.IsPinned)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxPinned)
            .Select(p => new JumpListEntry(p, PinnedCategory))
            .ToList();

        var recent = all
            .Where(p => !p.IsPinned && p.LastOpenedAt is not null)
            .OrderByDescending(p => p.LastOpenedAt)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxTotal - pinned.Count)
            .Select(p => new JumpListEntry(p, RecentCategory));

        return [.. pinned, .. recent];
    }
}
