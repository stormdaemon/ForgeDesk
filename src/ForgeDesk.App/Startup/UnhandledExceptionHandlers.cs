using System.Windows;
using System.Windows.Threading;
using ForgeDesk.App.Dialogs;
using ForgeDesk.App.Services;
using ForgeDesk.Core.Common;
using Serilog;

namespace ForgeDesk.App.Startup;

/// <summary>
/// Last-resort error handling. UI-thread exceptions are logged and shown in a friendly dialog
/// while the app keeps running; unobserved task exceptions are logged; a fatal exception on
/// another thread is logged with a crash report before the process ends.
/// </summary>
internal sealed class UnhandledExceptionHandlers
{
    // A failure repeating in a loop (layout, timers) must not bury the user under dialogs.
    private const int MaxDialogsPerBurst = 3;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(30);

    private readonly Func<IAppPaths?> _paths;
    private readonly Action<string> _openFolder;
    private readonly Queue<DateTimeOffset> _recentDialogs = new();
    private bool _dialogOpen;

    public UnhandledExceptionHandlers(Func<IAppPaths?> paths, Action<string> openFolder)
    {
        _paths = paths;
        _openFolder = openFolder;
    }

    public void Register(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread");
        e.Handled = true;
        ShowFriendlyError(e.Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Warning(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString());
        Log.Fatal(exception, "Fatal unhandled exception (terminating: {Terminating})", e.IsTerminating);
        if (_paths() is { } paths && CrashReport.TryWrite(paths.LogsDirectory, exception) is { } report)
        {
            Log.Fatal("Crash report written to {Report}", report);
        }

        Log.CloseAndFlush();
    }

    private void ShowFriendlyError(Exception exception)
    {
        if (_dialogOpen || !AllowDialog())
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var error = exception is ForgeException
                ? ErrorInfo.From(exception)
                : new ErrorInfo(ErrorKind.Unknown, "Something went wrong",
                    "ForgeDesk ran into an unexpected problem. Your work is safe and you can keep using the app.",
                    "If this keeps happening, restart ForgeDesk and share the details below when reporting the problem.",
                    exception.ToString());
            Action? openLogs = _paths() is { } paths ? () => _openFolder(paths.LogsDirectory) : null;
            new ErrorDialog(error, openLogs).ShowModal(DialogService.ActiveWindow());
        }
        catch (Exception dialogFailure)
        {
            Log.Error(dialogFailure, "Could not show the error dialog");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private bool AllowDialog()
    {
        var now = DateTimeOffset.UtcNow;
        while (_recentDialogs.Count > 0 && now - _recentDialogs.Peek() > BurstWindow)
        {
            _recentDialogs.Dequeue();
        }

        if (_recentDialogs.Count >= MaxDialogsPerBurst)
        {
            return false;
        }

        _recentDialogs.Enqueue(now);
        return true;
    }
}
