using System.Windows;
using System.Windows.Controls;
using ForgeDesk.App.Activation;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Services.Notifications;

/// <summary>
/// In-app notifications as WPF-UI snackbars (one at a time, queued), plus Windows toasts when
/// ForgeDesk is in the background. Successes fade after a few seconds; errors stay until
/// dismissed and offer "Details". Safe to call from any thread.
/// </summary>
internal sealed class NotificationService : INotificationService
{
    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(7);

    // Informational messages that waited this long behind an error are no longer worth showing.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    private readonly IUiDispatcher _dispatcher;
    private readonly IServiceProvider _services;
    private readonly AppActivationService _activation;
    private readonly ILogger<NotificationService> _logger;
    private readonly Queue<Notification> _queue = new();
    private SnackbarPresenter? _presenter;
    private Notification? _current;
    private bool _pumping;
    private bool _toastsRegistered;

    // IServiceProvider: IDialogService is resolved when "Details" is clicked, avoiding a cycle.
    public NotificationService(IUiDispatcher dispatcher, IServiceProvider services, AppActivationService activation,
        ILogger<NotificationService> logger)
    {
        _dispatcher = dispatcher;
        _services = services;
        _activation = activation;
        _logger = logger;
    }

    /// <summary>Registers the toast click handler; call once, early during startup.</summary>
    public void RegisterToastActivation()
    {
        if (_toastsRegistered)
        {
            return;
        }

        try
        {
            ToastNotifications.RegisterActivationHandler(projectId =>
                _activation.Activate(projectId is null ? ActivationRequest.Empty : ActivationRequest.ForProject(projectId)));
            _toastsRegistered = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toast activation is unavailable");
        }
    }

    /// <summary>Connects the snackbar host of the main window and shows anything queued so far.</summary>
    public void AttachHost(SnackbarPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        _dispatcher.Post(() =>
        {
            _presenter = presenter;
            _ = PumpAsync();
        });
    }

    public void Show(string title, string? message = null, NotificationSeverity severity = NotificationSeverity.Info, NotificationAction? action = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Enqueue(new Notification(title, message, severity, action, null, DateTimeOffset.UtcNow));
    }

    public void ShowError(ErrorInfo error, NotificationAction? action = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        Enqueue(new Notification(error.Title, error.Message, NotificationSeverity.Error, action, error, DateTimeOffset.UtcNow));
    }

    public void ShowSystemNotification(string title, string message, string? projectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _dispatcher.Post(() =>
        {
            if (Application.Current?.MainWindow is { IsActive: true })
            {
                return;
            }

            try
            {
                ToastNotifications.Show(title, message, projectId);
            }
            catch (Exception ex)
            {
                // Notifications can be disabled by policy or unavailable on the OS edition.
                _logger.LogWarning(ex, "Could not show a Windows notification");
            }
        });
    }

    private void Enqueue(Notification notification) => _dispatcher.Post(() =>
    {
        if (IsDuplicate(notification))
        {
            return;
        }

        _queue.Enqueue(notification);
        _ = PumpAsync();
    });

    private bool IsDuplicate(Notification notification) =>
        (_current is { } current && current.SameAs(notification)) || _queue.Any(queued => queued.SameAs(notification));

    private async Task PumpAsync()
    {
        if (_pumping || _presenter is null)
        {
            return;
        }

        _pumping = true;
        try
        {
            while (_presenter is { } presenter && _queue.TryDequeue(out var next))
            {
                if (next.Severity is NotificationSeverity.Info or NotificationSeverity.Success
                    && DateTimeOffset.UtcNow - next.CreatedAt > StaleAfter)
                {
                    continue;
                }

                _current = next;
                await presenter.ImmediatelyDisplay(CreateSnackbar(presenter, next)).ConfigureAwait(true);

                // When dismissed early, the presenter finishes its hide animation (and resets its
                // cancellation state) after ImmediatelyDisplay returns; wait for it.
                while (presenter.Content is not null)
                {
                    await Task.Delay(50).ConfigureAwait(true);
                }

                _current = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification display failed");
        }
        finally
        {
            _current = null;
            _pumping = false;
        }
    }

    private Snackbar CreateSnackbar(SnackbarPresenter presenter, Notification notification)
    {
        var (symbol, iconBrushKey, appearance) = notification.Severity switch
        {
            NotificationSeverity.Success => (SymbolRegular.CheckmarkCircle24, "ForgeSuccessBrush", ControlAppearance.Secondary),
            NotificationSeverity.Warning => (SymbolRegular.Warning24, (string?)null, ControlAppearance.Caution),
            NotificationSeverity.Error => (SymbolRegular.ErrorCircle24, (string?)null, ControlAppearance.Danger),
            _ => (SymbolRegular.Info24, "ForgeInfoBrush", ControlAppearance.Secondary),
        };

        var icon = new SymbolIcon { Symbol = symbol };
        if (iconBrushKey is not null)
        {
            icon.SetResourceReference(IconElement.ForegroundProperty, iconBrushKey);
        }

        var snackbar = new Snackbar(presenter)
        {
            Title = notification.Title,
            Appearance = appearance,
            Icon = icon,
            IsCloseButtonEnabled = true,
            Timeout = notification.Severity switch
            {
                NotificationSeverity.Error => Timeout.InfiniteTimeSpan,
                NotificationSeverity.Warning => WarningDuration,
                _ => InfoDuration,
            },
        };
        snackbar.Content = CreateBody(presenter, notification);
        return snackbar;
    }

    private StackPanel CreateBody(SnackbarPresenter presenter, Notification notification)
    {
        var body = new StackPanel();
        if (!string.IsNullOrWhiteSpace(notification.Message))
        {
            body.Children.Add(new System.Windows.Controls.TextBlock { Text = notification.Message, TextWrapping = TextWrapping.Wrap });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (notification.Action is { } action)
        {
            actions.Children.Add(CreateButton(action.Label, async () =>
            {
                await presenter.HideCurrent().ConfigureAwait(true);
                await RunActionAsync(action).ConfigureAwait(true);
            }));
        }

        if (notification.Error is { } error)
        {
            actions.Children.Add(CreateButton("Details", () =>
                _services.GetRequiredService<IDialogService>().ShowErrorAsync(error)));
        }

        if (actions.Children.Count > 0)
        {
            body.Children.Add(actions);
        }

        return body;
    }

    private System.Windows.Controls.Button CreateButton(string label, Func<Task> onClick)
    {
        var button = new Wpf.Ui.Controls.Button { Content = label, Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += async (_, _) =>
        {
            try
            {
                await onClick().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification button failed");
            }
        };
        return button;
    }

    private async Task RunActionAsync(NotificationAction action)
    {
        try
        {
            await action.Execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError(ErrorInfo.From(ex, $"{action.Label} failed"));
        }
    }

    private sealed record Notification(
        string Title,
        string? Message,
        NotificationSeverity Severity,
        NotificationAction? Action,
        ErrorInfo? Error,
        DateTimeOffset CreatedAt)
    {
        public bool SameAs(Notification other) =>
            Severity == other.Severity
            && string.Equals(Title, other.Title, StringComparison.Ordinal)
            && string.Equals(Message, other.Message, StringComparison.Ordinal);
    }
}
