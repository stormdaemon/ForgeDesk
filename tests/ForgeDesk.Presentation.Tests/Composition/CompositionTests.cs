using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.DependencyInjection;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Security;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Composition;

/// <summary>
/// Builds the real container (Core + Presentation) with test doubles only for the Windows
/// services, and checks every registration can actually be constructed.
/// </summary>
public sealed class CompositionTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "forgedesk-tests", "composition-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider BuildProvider()
    {
        var paths = new AppPaths(_dataDir);
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
        services.AddSingleton<ISecretStore, InMemorySecretStore>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Container_validates_on_build()
    {
        using var provider = BuildProvider();
        provider.Should().NotBeNull();
    }

    [Theory]
    [InlineData(typeof(ISettingsService))]
    [InlineData(typeof(IProjectRegistry))]
    [InlineData(typeof(IProjectStatusService))]
    [InlineData(typeof(IProjectCloneService))]
    [InlineData(typeof(IProjectDetector))]
    [InlineData(typeof(ICustomCommandStore))]
    [InlineData(typeof(IGitService))]
    [InlineData(typeof(IGitHubService))]
    [InlineData(typeof(IGitHubAccountService))]
    [InlineData(typeof(IRunService))]
    [InlineData(typeof(IWorkItemService))]
    [InlineData(typeof(IActivityLog))]
    [InlineData(typeof(IFileService))]
    [InlineData(typeof(IFileIndex))]
    [InlineData(typeof(IContentSearchService))]
    [InlineData(typeof(IProjectWatcher))]
    [InlineData(typeof(IProjectAnalyzer))]
    [InlineData(typeof(IReleaseService))]
    [InlineData(typeof(IShellDiscovery))]
    [InlineData(typeof(INavigationService))]
    [InlineData(typeof(IBackgroundOperations))]
    [InlineData(typeof(ShellViewModel))]
    public void Every_contract_resolves(Type contract)
    {
        using var provider = BuildProvider();
        provider.GetRequiredService(contract).Should().NotBeNull();
    }

    [Fact]
    public void GitHub_token_is_offered_to_git_through_a_credential_provider()
    {
        using var provider = BuildProvider();
        provider.GetServices<IGitCredentialProvider>().Should().NotBeEmpty();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
