using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Activity;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Insights;
using ForgeDesk.Presentation.Overview;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Overview;

/// <summary>The Overview, Insights and Activity tabs and the Activity page are built by the application container.</summary>
public sealed class GlanceCompositionTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(WorkspaceSection.Overview, typeof(OverviewViewModel))]
    [InlineData(WorkspaceSection.Insights, typeof(InsightsViewModel))]
    [InlineData(WorkspaceSection.Activity, typeof(ActivitySectionViewModel))]
    public void The_tabs_are_created_with_the_project_context(WorkspaceSection section, Type expected)
    {
        using var provider = Build();
        var factory = provider.GetRequiredService<IWorkspaceSectionFactory>();
        using var context = new ProjectContext(TestData.Project("forge-app", _folder.Path), provider.GetRequiredService<IGitService>(),
            provider.GetRequiredService<IProjectDetector>(), provider.GetRequiredService<IProjectRegistry>(), ImmediateDispatcher.Instance);

        var created = factory.Create(section, context);

        factory.IsAvailable(section).Should().BeTrue();
        created.Should().BeOfType(expected);
        created.Section.Should().Be(section);
        ((IDisposable)created).Dispose();
    }

    [Fact]
    public async Task The_overview_is_the_first_tab_a_project_opens_on()
    {
        using var provider = Build();
        using var workspace = provider.GetRequiredService<IProjectWorkspaceFactory>().Create(TestData.Project("forge-app", _folder.Path));

        await workspace.OnNavigatedToAsync(null);

        workspace.CurrentSection.Should().BeOfType<OverviewViewModel>();
    }

    [Fact]
    public void The_activity_page_is_available()
    {
        using var provider = Build();
        var pages = provider.GetRequiredService<IPageFactory>();

        pages.IsAvailable(PageKind.Activity).Should().BeTrue();
        var page = pages.Create(PageKind.Activity);

        page.Should().BeOfType<GlobalActivityViewModel>();
        ((IDisposable)page!).Dispose();
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
            typeof(IGitHubAccountService), typeof(IGitHubService), typeof(IFileService), typeof(IProjectAnalyzer),
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
