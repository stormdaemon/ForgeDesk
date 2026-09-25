using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ForgeDesk.App.Activation;
using ForgeDesk.App.Composition;
using ForgeDesk.App.Dialogs;
using ForgeDesk.App.Services;
using ForgeDesk.App.Services.Notifications;
using ForgeDesk.App.Services.Windowing;
using ForgeDesk.App.Startup;
using ForgeDesk.App.Theming;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.DependencyInjection;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;
using ForgeDesk.Presentation;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ForgeDesk.App;

/// <summary>
/// Composition root and startup sequence: logging, global error handling, DI container,
/// database (with corruption recovery), settings, theme, then the main window.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan SplashDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(5);

    private IAppPaths? _paths;
    private ServiceProvider? _services;
    private ActivationPipeServer? _activationServer;
    private Microsoft.Extensions.Logging.ILogger? _logger;

    /// <summary>Names of the single-instance mutex and pipe, provided by <see cref="Program"/>.</summary>
    internal InstanceIdentity? Identity { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        new UnhandledExceptionHandlers(() => _paths, OpenFolderSafely).Register(this);
        _ = StartAsync(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services is not null)
            {
                RunBounded(() => _services.GetRequiredService<WindowPlacementService>().SaveAsync());
            }

            if (_activationServer is not null)
            {
                RunBounded(() => _activationServer.DisposeAsync().AsTask());
            }

            if (_services is not null)
            {
                // Synchronous disposals run here on the UI thread (some services own dispatcher
                // objects); asynchronous ones finish on the pool, bounded so exit never hangs.
                if (!_services.DisposeAsync().AsTask().Wait(ShutdownBudget))
                {
                    Log.Warning("Services did not finish disposing within {Budget}", ShutdownBudget);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while shutting down");
        }
        finally
        {
            Log.Information("ForgeDesk exited with code {ExitCode}", e.ApplicationExitCode);
            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }

    private async Task StartAsync(string[] arguments)
    {
        SplashWindow? splash = null;
        var splashTimer = new DispatcherTimer(SplashDelay, DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            splash = new SplashWindow();
            splash.Show();
        }, Dispatcher);

        try
        {
            var paths = AppPaths.CreateDefault();
            paths.EnsureCreated();
            _paths = paths;
            Log.Logger = AppLogging.Create(paths);
            Log.Information("ForgeDesk {Version} starting on {OperatingSystem}, {Runtime}", AppInfo.Version, AppInfo.OperatingSystem, AppInfo.Runtime);

            _services = BuildServices(paths);
            _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("ForgeDesk.Startup");

            // Before any child process starts, so that all of them die with ForgeDesk.
            _services.GetRequiredService<ProcessJobObject>().AttachCurrentProcess();

            var activation = _services.GetRequiredService<AppActivationService>();
            StartActivationServer(activation);
            var notifications = _services.GetRequiredService<NotificationService>();
            notifications.RegisterToastActivation();

            var database = _services.GetRequiredService<Database>();
            var databaseState = await database.InitializeAsync().ConfigureAwait(true);
            _ = Task.Run(() => BackupDatabaseAsync(database));

            await _services.GetRequiredService<ISettingsService>().LoadAsync().ConfigureAwait(true);
            _services.GetRequiredService<ThemeService>().Initialize();
            await RecoverInterruptedRunsAsync().ConfigureAwait(true);
            _ = Task.Run(RestoreGitHubSessionAsync);

            var window = ShowMainWindow(activation, notifications);
            if (databaseState.Recovered)
            {
                NotifyDatabaseRecovered(databaseState, notifications);
            }

            activation.Activate(ActivationRequest.Parse(arguments, Environment.CurrentDirectory), bringToFront: false);
            _logger.LogInformation("Startup complete ({Width}×{Height})", window.Width, window.Height);
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
        }
        finally
        {
            splashTimer.Stop();
            splash?.Close();
        }
    }

    private static ServiceProvider BuildServices(IAppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Debug).AddSerilog(Log.Logger, dispose: false));
        services.AddForgeDeskCore(paths);
        services.AddForgeDeskPresentation();
        services.AddForgeDeskWindowsServices();
        return services.BuildServiceProvider();
    }

    private MainWindow ShowMainWindow(AppActivationService activation, NotificationService notifications)
    {
        var services = _services!;
        var window = services.GetRequiredService<MainWindow>();
        window.DataContext = services.GetKeyedService<object>(WindowsServices.ShellViewModelKey);
        if (window.DataContext is null)
        {
            _logger?.LogWarning("No shell view model is registered under {Key}; showing an empty window", WindowsServices.ShellViewModelKey);
        }

        var placement = services.GetRequiredService<WindowPlacementService>();
        placement.Restore(window);
        placement.Track(window);

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        services.GetRequiredService<ThemeService>().AttachWindow(window);
        notifications.AttachHost(window.SnackbarPresenter);
        activation.AttachWindow(window);
        StartOptional("taskbar progress", () => services.GetRequiredService<TaskbarProgressService>().Attach(window));
        StartOptional("jump list", () => services.GetRequiredService<JumpListService>().Start());
        return window;
    }

    private void StartActivationServer(AppActivationService activation)
    {
        if (Identity is null)
        {
            return;
        }

        _activationServer = new ActivationPipeServer(Identity.PipeName, message =>
        {
            activation.Activate(ActivationRequest.Parse(message.Arguments, message.WorkingDirectory));
            return Task.CompletedTask;
        }, _services?.GetRequiredService<ILoggerFactory>().CreateLogger<ActivationPipeServer>());
        _activationServer.Start();
    }

    private async Task BackupDatabaseAsync(Database database)
    {
        try
        {
            if (await database.BackupIfStaleAsync(TimeSpan.FromDays(1)).ConfigureAwait(false) is { } backup)
            {
                _logger?.LogInformation("Daily database backup written to {Backup}", backup);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Daily database backup failed");
        }
    }

    private async Task RecoverInterruptedRunsAsync()
    {
        try
        {
            var runs = _services!.GetRequiredService<IRunService>();
            var recovered = await runs.RecoverInterruptedRunsAsync().ConfigureAwait(true);
            if (recovered > 0)
            {
                _logger?.LogInformation("Marked {Count} runs from the previous session as interrupted", recovered);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not recover interrupted runs");
        }
    }

    private async Task RestoreGitHubSessionAsync()
    {
        try
        {
            var account = await _services!.GetRequiredService<IGitHubAccountService>().RestoreAsync().ConfigureAwait(false);
            _logger?.LogInformation("GitHub session: {State}", account is null ? "signed out" : "restored");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not restore the GitHub session");
        }
    }

    private void NotifyDatabaseRecovered(DatabaseOpenResult state, NotificationService notifications)
    {
        var (title, message) = DatabaseRecoveryMessage.For(state, CultureInfo.CurrentCulture);
        _logger?.LogWarning("Database recovered: {Message} (quarantined copy: {Quarantined})", message, state.QuarantinedPath);
        var backups = _paths?.BackupsDirectory;
        notifications.Show(title, message, NotificationSeverity.Warning,
            backups is null ? null : new NotificationAction("Show backups", () =>
            {
                OpenFolderSafely(backups);
                return Task.CompletedTask;
            }));
    }

    private void ReportStartupFailure(Exception exception)
    {
        Log.Fatal(exception, "ForgeDesk could not start");
        try
        {
            var error = ErrorInfo.From(exception, "ForgeDesk could not start");
            Action? openLogs = _paths is { } paths ? () => OpenFolderSafely(paths.LogsDirectory) : null;
            new ErrorDialog(error, openLogs).ShowModal(null);
        }
        catch (Exception dialogFailure)
        {
            Log.Error(dialogFailure, "Could not show the startup error");
        }

        Shutdown(1);
    }

    private void StartOptional(string feature, Action start)
    {
        try
        {
            start();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The {Feature} integration is unavailable", feature);
        }
    }

    private void OpenFolderSafely(string folder)
    {
        try
        {
            if (_services?.GetService<IShellIntegration>() is { } shell)
            {
                shell.OpenFolder(folder);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Folder}", folder);
        }
    }

    private static void RunBounded(Func<Task> work)
    {
        try
        {
            // Off the UI thread: continuations must not depend on a dispatcher that is shutting down.
            if (!Task.Run(work).Wait(ShutdownBudget))
            {
                Log.Warning("A shutdown step did not finish within {Budget}", ShutdownBudget);
            }
        }
        catch (AggregateException ex)
        {
            Log.Warning(ex.GetBaseException(), "A shutdown step failed");
        }
    }
}
