using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Commands;

/// <summary>
/// "Run: &lt;name&gt;" entries for the commands of the project on screen (detected and custom).
/// Running one starts it and shows it in the Commands tab.
/// </summary>
public sealed class CommandsPaletteSource : IPaletteSource
{
    private readonly INavigationService _navigation;
    private readonly ICustomCommandStore _customCommands;
    private readonly IRunService _runs;
    private readonly INotificationService _notifications;
    private readonly ILogger<CommandsPaletteSource> _logger;

    public CommandsPaletteSource(INavigationService navigation, IRunService runs, INotificationService notifications,
        ILogger<CommandsPaletteSource> logger, ICustomCommandStore? customCommands = null)
    {
        _navigation = navigation;
        _customCommands = customCommands ?? NoCustomCommandStore.Instance;
        _runs = runs;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.Wants(PaletteCategory.Command)
            || _navigation.CurrentPage is not ProjectWorkspaceViewModel workspace
            || !workspace.AvailableTabs.Any(t => t.Section == WorkspaceSection.Commands))
        {
            return [];
        }

        var context = workspace.Context;
        IReadOnlyList<DetectedCommand> custom;
        try
        {
            custom = await _customCommands.GetAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the custom commands for the palette");
            custom = [];
        }

        var running = _runs.ActiveRuns
            .Where(r => string.Equals(r.Request.ProjectId, context.ProjectId, StringComparison.Ordinal) && r.Status is RunStatus.Running or RunStatus.Queued)
            .Select(r => r.Request.CommandId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        return (context.Profile?.Commands ?? [])
            .Concat(custom)
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .Select(command => new PaletteItem
            {
                Title = $"Run: {command.Name}",
                Subtitle = $"{workspace.Name} · {command.CommandLine}" + (running.Contains(command.Id) ? " · running" : string.Empty),
                Icon = command.Category switch
                {
                    CommandCategory.Build => "Wrench20",
                    CommandCategory.Test => "Beaker20",
                    _ => "Play20",
                },
                Category = PaletteCategory.Command,
                Keywords = $"run {CommandCategories.Name(command.Category)} {command.Source} {command.CommandLine}",
                // Dev / Build / Test first, as in the workspace header.
                Boost = Math.Max(0, 2 - (CommandCategories.Rank(command.Category) * 0.5)),
                Execute = () => RunAsync(workspace, command),
            })
            .ToList();
    }

    private async Task RunAsync(ProjectWorkspaceViewModel workspace, DetectedCommand command)
    {
        try
        {
            var request = CommandsSectionViewModel.CreateRequest(workspace.Context, command);
            var session = await _runs.StartAsync(request, workspace.Context.Lifetime).ConfigureAwait(true);
            await workspace.SelectSectionAsync(WorkspaceSection.Commands, session.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, $"Could not start {command.Name}"));
        }
    }
}
