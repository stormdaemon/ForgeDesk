using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Commands;

/// <summary>
/// Tells the user when a command finishes, for every project: a Windows notification (shown by the
/// notification service only while ForgeDesk is in the background) and, for failures, an in-app
/// notification whose "View log" action opens the run in the Commands tab. Cancelled runs are
/// silent (the user stopped them). Created at startup, lives as long as the application.
/// </summary>
public sealed class RunNotificationsCoordinator : IDisposable
{
    private readonly IRunService _runs;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IProjectRegistry _registry;
    private readonly IProjectActions _projectActions;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<RunNotificationsCoordinator> _logger;
    private bool _disposed;

    public RunNotificationsCoordinator(
        IRunService runs,
        ISettingsService settings,
        INotificationService notifications,
        IProjectRegistry registry,
        IProjectActions projectActions,
        IUiDispatcher dispatcher,
        ILogger<RunNotificationsCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
        _settings = settings;
        _notifications = notifications;
        _registry = registry;
        _projectActions = projectActions;
        _dispatcher = dispatcher;
        _logger = logger;
        _runs.RunCompleted += OnRunCompleted;
    }

    /// <summary>Last notification task (tests await it).</summary>
    internal Task LastNotification { get; private set; } = Task.CompletedTask;

    /// <summary>Whether a finished run deserves a notification.</summary>
    public static bool ShouldNotify(RunRecord record, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.NotifyWhenRunCompletes && record.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Interrupted;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _runs.RunCompleted -= OnRunCompleted;
        }
    }

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e)
    {
        if (_disposed || !ShouldNotify(e.Record, _settings.Current))
        {
            return;
        }

        LastNotification = NotifyAsync(e.Record);
    }

    private async Task NotifyAsync(RunRecord record)
    {
        try
        {
            var project = await ProjectNameAsync(record.ProjectId).ConfigureAwait(false);
            var duration = Format.Duration(record.Duration);
            var (title, message) = record.Status switch
            {
                RunStatus.Succeeded => ($"{record.Label} succeeded", $"{project} · finished in {duration}"),
                RunStatus.Failed => ($"{record.Label} failed",
                    $"{project} · " + (record.ExitCode is { } code ? $"exit code {code}" : "failed") + $" after {duration}"),
                _ => ($"{record.Label} was interrupted", $"{project} · the command stopped before finishing"),
            };

            await _dispatcher.InvokeAsync(() =>
            {
                _notifications.ShowSystemNotification(title, message, record.ProjectId);
                if (record.Status != RunStatus.Succeeded)
                {
                    var detail = string.IsNullOrWhiteSpace(record.ErrorSummary) ? message : $"{message}\n{FirstLines(record.ErrorSummary, 3)}";
                    _notifications.Show(title, detail, NotificationSeverity.Error,
                        new NotificationAction("View log", () => ViewLogAsync(record)));
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A notification must never break the run pipeline.
            _logger.LogWarning(ex, "Could not notify the completion of run {RunId}", record.Id);
        }
    }

    private Task ViewLogAsync(RunRecord record) => _projectActions.OpenAsync(record.ProjectId, WorkspaceSection.Commands, record.Id);

    private async Task<string> ProjectNameAsync(string projectId)
    {
        try
        {
            return (await _registry.GetAsync(projectId).ConfigureAwait(false))?.Name ?? "ForgeDesk";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read project {ProjectId}", projectId);
            return "ForgeDesk";
        }
    }

    private static string FirstLines(string text, int count) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(count).Select(l => l.TrimEnd('\r')));
}
