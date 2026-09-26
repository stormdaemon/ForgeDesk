using System.Windows;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.App.Activation;

/// <summary>Raised when ForgeDesk is asked to open a project or folder.</summary>
public sealed class AppActivationEventArgs : EventArgs
{
    public AppActivationEventArgs(ActivationRequest request) => Request = request;

    public ActivationRequest Request { get; }

    /// <summary>Set by a subscriber that fully handled the request (skips the default handling).</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Activation requests coming from the command line, a second instance (jump list, "ForgeDesk ."
/// in a terminal) or a toast click. The shell subscribes to decide what to show.
/// </summary>
public interface IAppActivationHandler
{
    /// <summary>
    /// Raised on the UI thread after the main window was brought to the front. When no subscriber
    /// sets <see cref="AppActivationEventArgs.Handled"/>, ForgeDesk opens the project itself
    /// (registering the folder first when needed) through INavigationService.
    /// </summary>
    event EventHandler<AppActivationEventArgs>? Activated;
}

/// <summary>
/// Single entry point for activations. Requests arriving before the main window exists are
/// kept and replayed once <see cref="AttachWindow"/> is called.
/// </summary>
internal sealed class AppActivationService : IAppActivationHandler
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IServiceProvider _services;
    private readonly ILogger<AppActivationService> _logger;
    private readonly List<ActivationRequest> _pending = [];
    private Window? _window;

    // IServiceProvider breaks the cycle navigation → shell → notifications → activation.
    public AppActivationService(IUiDispatcher dispatcher, IServiceProvider services, ILogger<AppActivationService> logger)
    {
        _dispatcher = dispatcher;
        _services = services;
        _logger = logger;
    }

    public event EventHandler<AppActivationEventArgs>? Activated;

    /// <summary>Starts delivering activations to <paramref name="window"/> (the main window).</summary>
    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _dispatcher.Post(() =>
        {
            _window = window;
            var pending = _pending.ToList();
            _pending.Clear();
            foreach (var request in pending)
            {
                Handle(request, bringToFront: false);
            }
        });
    }

    /// <summary>Handles a request from any thread.</summary>
    public void Activate(ActivationRequest request, bool bringToFront = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        _dispatcher.Post(() => Handle(request, bringToFront));
    }

    private void Handle(ActivationRequest request, bool bringToFront)
    {
        if (_window is null)
        {
            if (!request.IsEmpty)
            {
                _pending.Add(request);
            }

            return;
        }

        if (bringToFront)
        {
            WindowActivator.BringToFront(_window);
        }

        if (request.IsEmpty)
        {
            return;
        }

        _logger.LogInformation("Activation: project {ProjectId}, folder {Folder}", request.ProjectId ?? "-", request.FolderPath ?? "-");
        var args = new AppActivationEventArgs(request);
        foreach (var handler in Activated?.GetInvocationList().Cast<EventHandler<AppActivationEventArgs>>() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An activation subscriber failed");
            }
        }

        if (!args.Handled)
        {
            _ = OpenAsync(request);
        }
    }

    private async Task OpenAsync(ActivationRequest request)
    {
        var navigation = _services.GetService<INavigationService>();
        if (navigation is null)
        {
            _logger.LogWarning("No navigation service is available to handle the activation");
            return;
        }

        try
        {
            var projectId = request.ProjectId ?? await ResolveFolderAsync(request.FolderPath).ConfigureAwait(true);
            if (projectId is not null)
            {
                await navigation.OpenProjectAsync(projectId).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the requested project");
            _services.GetService<INotificationService>()?.ShowError(ErrorInfo.From(ex, "Could not open the project"));
        }
    }

    private async Task<string?> ResolveFolderAsync(string? folder)
    {
        if (folder is null || _services.GetService<IProjectRegistry>() is not { } registry)
        {
            return null;
        }

        var existing = await registry.FindByPathAsync(folder).ConfigureAwait(true);
        return existing?.Id ?? (await registry.AddAsync(folder).ConfigureAwait(true)).Id;
    }
}
