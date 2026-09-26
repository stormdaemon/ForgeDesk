using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Git;

/// <summary>The Git tab and its palette source are built by the application container.</summary>
public sealed class GitCompositionTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task The_git_tab_is_created_by_the_workspace_with_its_services()
    {
        using var provider = Build();
        var project = TestData.Project("forge-app", _folder.Path);

        using var workspace = provider.GetRequiredService<IProjectWorkspaceFactory>().Create(project);
        await workspace.SelectSectionAsync(WorkspaceSection.Git);

        workspace.CurrentSection.Should().BeOfType<GitSectionViewModel>();
        provider.GetServices<IPaletteSource>().Should().ContainSingle(s => s is GitPaletteSource);
        provider.GetRequiredService<GitViewPreferences>().Should().BeSameAs(provider.GetRequiredService<GitViewPreferences>());
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
            typeof(IGitHubAccountService),
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
