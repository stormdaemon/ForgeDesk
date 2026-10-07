using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Commands;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Tasks;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Commands;

/// <summary>The Commands and Tasks tabs, their palette sources and the run notifications are built by the application container.</summary>
public sealed class CommandsAndTasksCompositionTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task The_tabs_palette_sources_and_coordinator_are_registered()
    {
        using var provider = Build();
        var project = TestData.Project("forge-app", _folder.Path);

        using var workspace = provider.GetRequiredService<IProjectWorkspaceFactory>().Create(project);
        await workspace.SelectSectionAsync(WorkspaceSection.Commands);
        workspace.CurrentSection.Should().BeOfType<CommandsSectionViewModel>();
        await workspace.SelectSectionAsync(WorkspaceSection.Tasks);
        workspace.CurrentSection.Should().BeOfType<TasksSectionViewModel>();

        provider.GetServices<IPaletteSource>().Should().ContainSingle(s => s is CommandsPaletteSource).And.ContainSingle(s => s is TasksPaletteSource);
        provider.GetRequiredService<RunNotificationsCoordinator>().Should().BeSameAs(provider.GetRequiredService<RunNotificationsCoordinator>());
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IUiDispatcher>(ImmediateDispatcher.Instance);
        foreach (var contract in new[]
        {
            typeof(IDialogService), typeof(INotificationService), typeof(IShellIntegration), typeof(IProjectRegistry), typeof(IProjectStatusService),
            typeof(IRunService), typeof(IWorkItemService), typeof(IActivityLog), typeof(IGitService), typeof(IProjectDetector), typeof(IProjectWatcher),
            typeof(IGitHubAccountService), typeof(ICustomCommandStore), typeof(IFileIndex),
        })
        {
            services.AddSingleton(contract, Substitute.For([contract], []));
        }

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(AppSettings.Default);
        services.AddSingleton(settings);
        services.AddForgeDeskPresentation();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
