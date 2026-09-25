using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Core.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers every platform-independent ForgeDesk service. The host must also register
    /// <see cref="Security.ISecretStore"/> (Windows Credential Manager in the app).
    /// </summary>
    public static IServiceCollection AddForgeDeskCore(this IServiceCollection services, IAppPaths paths)
    {
        services.TryAddSingleton(paths);
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IProcessRunner>(ProcessRunner.Instance);
        services.TryAddSingleton<Database>();
        services.TryAddSingleton<ISettingsService, SettingsService>();

        services
            .AddGitServices()
            .AddGitHubServices()
            .AddProjectsServices()
            .AddDetectionServices()
            .AddRunsServices()
            .AddWorkItemsServices()
            .AddActivityServices()
            .AddFilesServices()
            .AddAnalysisServices()
            .AddReleasesServices()
            .AddTerminalServices();

        return services;
    }
}
