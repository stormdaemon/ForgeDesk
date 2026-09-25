using System.IO.Pipes;
using ForgeDesk.App.Activation;
using ForgeDesk.App.Native;
using ForgeDesk.App.Services.Notifications;
using Velopack;

namespace ForgeDesk.App;

/// <summary>
/// Process entry point. Velopack must run first (install/update/uninstall hooks exit early),
/// then a single instance per user: later launches forward their arguments to the running
/// ForgeDesk, which comes to the front, and exit.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => RemoveToastRegistration())
            .Run();

        var identity = InstanceIdentity.ForCurrentUser();
        using var instance = SingleInstanceGuard.Acquire(identity.MutexName);
        if (!instance.IsPrimary)
        {
            return ForwardToPrimaryInstance(identity, args) ? 0 : 1;
        }

        var app = new App { Identity = identity };
        app.InitializeComponent();
        return app.Run();
    }

    private static bool ForwardToPrimaryInstance(InstanceIdentity identity, string[] args)
    {
        var message = new ActivationMessage(args, Environment.CurrentDirectory);

        // No SynchronizationContext exists yet, so blocking here cannot deadlock.
        return ActivationPipeClient
            .TrySendAsync(identity.PipeName, message, ForwardTimeout, AllowPrimaryToTakeForeground)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Windows only lets the foreground process hand focus to another one; this instance was
    /// just launched by the user, so it grants that right to the instance it forwards to.
    /// </summary>
    private static void AllowPrimaryToTakeForeground(NamedPipeClientStream pipe)
    {
        if (OperatingSystem.IsWindows() && NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            NativeMethods.AllowSetForegroundWindow(processId);
        }
    }

    private static void RemoveToastRegistration()
    {
        try
        {
            ToastNotifications.Unregister();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.IO.IOException)
        {
            // Uninstall must proceed even if the notification registration is already gone.
        }
    }
}
