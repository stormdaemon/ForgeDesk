using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Palette;

/// <summary>"Open any project": every registered project, pinned and recently opened ones first.</summary>
public sealed class ProjectsPaletteSource : IPaletteSource
{
    private readonly IProjectRegistry _registry;
    private readonly IProjectActions _actions;
    private readonly TimeProvider _time;
    private readonly ILogger<ProjectsPaletteSource> _logger;

    public ProjectsPaletteSource(IProjectRegistry registry, IProjectActions actions, ILogger<ProjectsPaletteSource> logger, TimeProvider? time = null)
    {
        _registry = registry;
        _actions = actions;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.Wants(PaletteCategory.Project))
        {
            return [];
        }

        try
        {
            var projects = await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            return projects.Select(project => new PaletteItem
            {
                Title = project.Name,
                Subtitle = project.Path,
                Icon = "Folder20",
                Category = PaletteCategory.Project,
                Keywords = project.GitHub?.FullName,
                Boost = Boost(project, query.CurrentProjectId, now),
                Execute = () => _actions.OpenAsync(project.Id),
            }).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list projects for the palette");
            return [];
        }
    }

    internal static double Boost(Project project, string? currentProjectId, DateTimeOffset now)
    {
        // The project on screen is the least useful destination.
        if (string.Equals(project.Id, currentProjectId, StringComparison.Ordinal))
        {
            return -2;
        }

        var boost = project.IsPinned ? 3.0 : 0.0;
        if (project.LastOpenedAt is { } opened)
        {
            var age = now - opened;
            boost += age < TimeSpan.FromDays(1) ? 3 : age < TimeSpan.FromDays(7) ? 2 : 1;
        }

        return boost;
    }
}
