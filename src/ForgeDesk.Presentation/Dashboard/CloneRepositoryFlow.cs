using ForgeDesk.Core.Common;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Dashboard;

/// <summary>
/// The "Clone repository" flow behind every clone entry point (sidebar, dashboard, palette,
/// Ctrl+Shift+O, onboarding): shows <see cref="CloneRepositoryDialogViewModel"/> and, from the
/// shell, opens the cloned project.
/// </summary>
public sealed class CloneRepositoryFlow : ICloneRequestHandler
{
    private readonly IServiceProvider _services;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly ILogger<CloneRepositoryFlow> _logger;
    private readonly Func<CloneRepositoryDialogViewModel> _createDialog;

    /// <param name="services">
    /// Resolves the dialog's services only when a clone is requested, so registering the flow costs
    /// nothing and does not force GitHub or git services into existence at startup.
    /// </param>
    public CloneRepositoryFlow(
        IServiceProvider services,
        IDialogService dialogs,
        INavigationService navigation,
        INotificationService notifications,
        ILogger<CloneRepositoryFlow> logger)
        : this(services, dialogs, navigation, notifications, logger, null)
    {
    }

    internal CloneRepositoryFlow(
        IServiceProvider services,
        IDialogService dialogs,
        INavigationService navigation,
        INotificationService notifications,
        ILogger<CloneRepositoryFlow> logger,
        Func<CloneRepositoryDialogViewModel>? createDialog)
    {
        _services = services;
        _dialogs = dialogs;
        _navigation = navigation;
        _notifications = notifications;
        _logger = logger;
        _createDialog = createDialog ?? (() => ActivatorUtilities.CreateInstance<CloneRepositoryDialogViewModel>(_services));
    }

    /// <summary>Shows the clone dialog; returns the cloned (and registered) project, or null when cancelled.</summary>
    public async Task<Project?> CloneAsync()
    {
        using var dialog = _createDialog();
        dialog.Start();
        bool? result;
        try
        {
            result = await _dialogs.ShowDialogAsync(dialog).ConfigureAwait(true);
        }
        finally
        {
            dialog.OnDialogClosed();
        }

        return result == true ? dialog.ClonedProject : null;
    }

    /// <summary>Clones, then opens the new project.</summary>
    public async Task RequestCloneAsync()
    {
        var project = await CloneAsync().ConfigureAwait(true);
        if (project is null)
        {
            return;
        }

        _notifications.Show($"Cloned {project.GitHub?.FullName ?? project.Name}", project.Path, NotificationSeverity.Success);
        try
        {
            await _navigation.OpenProjectAsync(project.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not open the cloned project {ProjectId}", project.Id);
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the cloned project"));
        }
    }
}
