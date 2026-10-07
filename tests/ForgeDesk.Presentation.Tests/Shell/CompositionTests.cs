using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Shell;

public sealed class CompositionTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void The_shell_and_its_services_resolve_from_the_container()
    {
        using var provider = Build(services => services.AddPage<DashboardPage>(PageKind.Dashboard));

        provider.GetRequiredService<ShellViewModel>().Should().NotBeNull();
        provider.GetRequiredService<INavigationService>().Should().BeSameAs(provider.GetRequiredService<NavigationService>());
        // Features add their own palette sources and the clone flow on top of the shell's.
        provider.GetServices<IPaletteSource>().Should().Contain(s => s is ProjectsPaletteSource)
            .And.Contain(s => s is NavigationPaletteSource)
            .And.Contain(s => s is ShellActionsPaletteSource);
        provider.GetRequiredService<IProjectActions>().CanClone.Should().Be(provider.GetServices<ICloneRequestHandler>().Any());
    }

    [Fact]
    public void Registered_pages_are_available_and_the_others_are_not()
    {
        using var provider = Build(services => services.AddPage<DashboardPage>(PageKind.Dashboard));
        var pages = provider.GetRequiredService<IPageFactory>();

        pages.IsAvailable(PageKind.Dashboard).Should().BeTrue();
        pages.Create(PageKind.Dashboard).Should().BeOfType<DashboardPage>();

        // Every page kind has a feature now; a factory knowing only the dashboard shows the fallback.
        var onlyDashboard = new PageFactory(provider, [new PageRegistration(PageKind.Dashboard, typeof(DashboardPage))]);
        onlyDashboard.IsAvailable(PageKind.Settings).Should().BeFalse();
        onlyDashboard.Create(PageKind.Settings).Should().BeNull();
    }

    [Fact]
    public void Project_workspaces_are_not_registered_as_pages()
    {
        var register = () => new ServiceCollection().AddPage<DashboardPage>(PageKind.Project);

        register.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_registered_clone_handler_enables_cloning()
    {
        using var provider = Build(services => services.AddSingleton(Substitute.For<ICloneRequestHandler>()));

        provider.GetRequiredService<IProjectActions>().CanClone.Should().BeTrue();
    }

    [Fact]
    public void The_workspace_factory_builds_a_workspace_with_registered_sections()
    {
        using var provider = Build(services => services.AddWorkspaceSection<RegisteredSection>(WorkspaceSection.Files));
        var project = TestData.Project("forge-app", _folder.Path);

        using var workspace = provider.GetRequiredService<IProjectWorkspaceFactory>().Create(project);

        workspace.ProjectId.Should().Be(project.Id);
        // Feature registrations (AddForgeDeskPresentation) add their own sections next to this one.
        workspace.AvailableTabs.Select(t => t.Section).Should().Contain(WorkspaceSection.Files);
    }

    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IUiDispatcher>(ImmediateDispatcher.Instance);
        AddSubstitute<IDialogService>(services);
        AddSubstitute<INotificationService>(services);
        AddSubstitute<IShellIntegration>(services);
        AddSubstitute<IProjectRegistry>(services);
        AddSubstitute<IProjectStatusService>(services);
        AddSubstitute<IRunService>(services);
        AddSubstitute<IWorkItemService>(services);
        AddSubstitute<IActivityLog>(services);
        AddSubstitute<IGitService>(services);
        AddSubstitute<IProjectDetector>(services);
        AddSubstitute<IProjectWatcher>(services);
        AddSubstitute<IGitHubAccountService>(services);
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(AppSettings.Default);
        services.AddSingleton(settings);
        services.AddForgeDeskPresentation();
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static void AddSubstitute<T>(IServiceCollection services)
        where T : class => services.AddSingleton(Substitute.For<T>());

    public sealed class DashboardPage;

    public sealed class RegisteredSection(ProjectContext context) : IWorkspaceSectionViewModel
    {
        public ProjectContext Context { get; } = context;

        public WorkspaceSection Section => WorkspaceSection.Files;

        public Task ActivateAsync() => Task.CompletedTask;

        public void Deactivate()
        {
        }
    }
}
