using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Presentation.Shell;

public static class ShellPresentationRegistration
{
    /// <summary>
    /// Registers the shell (main window view model, sidebar, status bar), navigation with its page
    /// factory, the project workspace frame and the shared project actions.
    /// </summary>
    public static IServiceCollection AddShellPresentation(this IServiceCollection services)
    {
        services.TryAddSingleton<IPageFactory, PageFactory>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<IProjectActions, ProjectActions>();

        services.AddSingleton<WorkspaceServices>();
        services.AddSingleton<IProjectWorkspaceFactory, ProjectWorkspaceFactory>();

        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<ShellViewModel>();
        return services;
    }
}
