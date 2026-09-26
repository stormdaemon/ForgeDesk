using ForgeDesk.Core.Common;
using ForgeDesk.Core.DependencyInjection;
using ForgeDesk.Core.Security;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Onboarding;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

/// <summary>The dashboard, onboarding and settings pages and the clone flow build from the real container.</summary>
public sealed class FeatureCompositionTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    [Theory]
    [InlineData(PageKind.Dashboard, typeof(DashboardViewModel))]
    [InlineData(PageKind.Onboarding, typeof(OnboardingViewModel))]
    [InlineData(PageKind.Settings, typeof(SettingsViewModel))]
    public void Pages_are_registered_and_created(PageKind kind, Type expected)
    {
        using var provider = Build();

        var page = provider.GetRequiredService<IPageFactory>().Create(kind);

        page.Should().BeOfType(expected);
        (page as IDisposable)?.Dispose();
    }

    [Fact]
    public void The_clone_flow_enables_cloning_everywhere()
    {
        using var provider = Build();

        provider.GetRequiredService<IProjectActions>().CanClone.Should().BeTrue();
        provider.GetRequiredService<ICloneRequestHandler>().Should().BeSameAs(provider.GetRequiredService<CloneRepositoryFlow>());
    }

    [Fact]
    public void Settings_contribute_palette_entries_and_a_placeholder_updater()
    {
        using var provider = Build();

        provider.GetServices<IPaletteSource>().Should().ContainSingle(s => s is SettingsPaletteSource);
        provider.GetRequiredService<IUpdateService>().Should().BeOfType<UnavailableUpdateService>();
        provider.GetRequiredService<UpdateCoordinator>().Status.Should().Be(UpdateStatus.Unsupported);
    }

    [Fact]
    public void A_host_updater_replaces_the_placeholder()
    {
        var updater = Substitute.For<IUpdateService>();
        using var provider = Build(services => services.AddSingleton(updater));

        provider.GetRequiredService<IUpdateService>().Should().BeSameAs(updater);
    }

    private ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var paths = new AppPaths(_folder.Combine("data"));
        paths.EnsureCreated();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddForgeDeskCore(paths);
        services.AddForgeDeskPresentation();
        services.AddSingleton<IUiDispatcher>(ImmediateDispatcher.Instance);
        services.AddSingleton(Substitute.For<IDialogService>());
        services.AddSingleton(Substitute.For<INotificationService>());
        services.AddSingleton(Substitute.For<IShellIntegration>());
        services.AddSingleton(Substitute.For<IThemeSwitcher>());
        services.AddSingleton(Substitute.For<ISecretStore>());
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
