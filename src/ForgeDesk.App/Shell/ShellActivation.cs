using ForgeDesk.App.Activation;
using ForgeDesk.Presentation.Shell;

namespace ForgeDesk.App.Shell;

/// <summary>
/// Routes window activations (command line, second instance, jump list, toast) to the shell, which
/// opens the project or registers and opens the folder, reporting failures as notifications.
/// </summary>
internal static class ShellActivation
{
    public static void Connect(IAppActivationHandler activation, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(shell);
        activation.Activated += (_, e) =>
        {
            e.Handled = true;
            _ = shell.HandleActivationAsync(e.Request.ProjectId, e.Request.FolderPath);
        };
    }
}
