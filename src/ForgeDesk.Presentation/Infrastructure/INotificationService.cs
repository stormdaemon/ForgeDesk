using ForgeDesk.Core.Common;

namespace ForgeDesk.Presentation.Infrastructure;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record NotificationAction(string Label, Func<Task> Execute);

public interface INotificationService
{
    /// <summary>In-app notification (snackbar). Errors stay until dismissed.</summary>
    void Show(string title, string? message = null, NotificationSeverity severity = NotificationSeverity.Info, NotificationAction? action = null);

    void ShowError(ErrorInfo error, NotificationAction? action = null);

    /// <summary>Windows toast, shown only when ForgeDesk is not the foreground window.</summary>
    void ShowSystemNotification(string title, string message, string? projectId = null);
}
