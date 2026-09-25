using Microsoft.Toolkit.Uwp.Notifications;

namespace ForgeDesk.App.Services.Notifications;

/// <summary>Windows toast notifications (Action Center) for an unpackaged app.</summary>
internal static class ToastNotifications
{
    private const string ProjectIdArgument = "projectId";
    private const string ActionArgument = "action";

    /// <summary>
    /// Subscribes to toast clicks. Call early at startup: when a click launches ForgeDesk,
    /// Windows delivers the activation as soon as a handler is registered.
    /// </summary>
    public static void RegisterActivationHandler(Action<string?> onActivated)
    {
        ArgumentNullException.ThrowIfNull(onActivated);
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var arguments = ToastArguments.Parse(e.Argument);
            onActivated(arguments.TryGetValue(ProjectIdArgument, out string? projectId) && !string.IsNullOrWhiteSpace(projectId) ? projectId : null);
        };
    }

    public static void Show(string title, string message, string? projectId)
    {
        var builder = new ToastContentBuilder().AddArgument(ActionArgument, "open");
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            builder.AddArgument(ProjectIdArgument, projectId);
        }

        builder.AddText(title);
        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.AddText(message);
        }

        builder.Show();
    }

    /// <summary>Removes ForgeDesk's toast registration (called by the uninstaller hook).</summary>
    public static void Unregister() => ToastNotificationManagerCompat.Uninstall();
}
