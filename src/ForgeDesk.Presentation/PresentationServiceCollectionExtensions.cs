using ForgeDesk.Presentation.Activity;
using ForgeDesk.Presentation.Commands;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Insights;
using ForgeDesk.Presentation.Onboarding;
using ForgeDesk.Presentation.Overview;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Releases;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tasks;
using ForgeDesk.Presentation.Terminal;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Presentation;

public static class PresentationServiceCollectionExtensions
{
    /// <summary>
    /// Registers all view models. The host must provide IUiDispatcher, IDialogService,
    /// INotificationService, IShellIntegration and INavigationService implementations.
    /// </summary>
    public static IServiceCollection AddForgeDeskPresentation(this IServiceCollection services)
    {
        services.TryAddSingleton<IBackgroundOperations, BackgroundOperations>();
        services.TryAddSingleton<IWorkspaceSectionFactory, WorkspaceSectionFactory>();

        services
            .AddShellPresentation()
            .AddDashboardPresentation()
            .AddOnboardingPresentation()
            .AddSettingsPresentation()
            .AddPalettePresentation()
            .AddOverviewPresentation()
            .AddGitPresentation()
            .AddGitHubPresentation()
            .AddFilesPresentation()
            .AddTerminalPresentation()
            .AddCommandsPresentation()
            .AddTasksPresentation()
            .AddReleasesPresentation()
            .AddInsightsPresentation()
            .AddActivityPresentation();

        return services;
    }
}
