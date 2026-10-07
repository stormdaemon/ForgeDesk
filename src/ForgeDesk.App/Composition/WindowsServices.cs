using System.Windows;
using ForgeDesk.App.Activation;
using ForgeDesk.App.Services;
using ForgeDesk.App.Services.Notifications;
using ForgeDesk.App.Services.Security;
using ForgeDesk.App.Services.Windowing;
using ForgeDesk.App.Shell;
using ForgeDesk.App.Theming;
using ForgeDesk.Core.Security;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.App.Composition;

/// <summary>Registers the Windows implementations of the Presentation abstractions and the app shell services.</summary>
public static class WindowsServices
{
    /// <summary>DI key of the view model rendered in MainWindow's ShellHost (the <see cref="ShellViewModel"/>).</summary>
    public const string ShellViewModelKey = "ForgeDesk.ShellViewModel";

    public static IServiceCollection AddForgeDeskWindowsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Application.Current.Dispatcher));
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IShellIntegration, ShellIntegration>();
        services.AddSingleton<ISecretStore, WindowsCredentialSecretStore>();

        services.AddSingleton<AppActivationService>();
        services.AddSingleton<IAppActivationHandler>(sp => sp.GetRequiredService<AppActivationService>());
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());

        services.AddSingleton<IThemeSwitcher, ThemeSwitcher>();
        Services.Updates.UpdateServicesRegistration.AddForgeDeskUpdates(services);

        // The main window renders the shell; activations (command line, jump list, toasts) go to it.
        services.AddKeyedSingleton<object>(ShellViewModelKey, (sp, _) =>
        {
            var shell = sp.GetRequiredService<ShellViewModel>();
            ShellActivation.Connect(sp.GetRequiredService<IAppActivationHandler>(), shell);
            return shell;
        });

        services.AddSingleton<ProcessJobObject>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<TaskbarProgressService>();
        services.AddSingleton<JumpListService>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
