using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Storage;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings.Support;

/// <summary>Everything a Settings view model needs, as fakes, with saves applied at once.</summary>
public sealed class SettingsHarness : IDisposable
{
    public SettingsHarness()
    {
        Paths = new AppPaths(Folder.Combine("data"));
        Paths.EnsureCreated();
        Database = new Database(Paths);
        Accounts.IsGitHubCliAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        Updates.CurrentVersion.Returns("1.4.0");
        StatusBar = new StatusBarViewModel(Navigation, Substitute.For<IRunService>(), new BackgroundOperations(ImmediateDispatcher.Instance), Accounts,
            Substitute.For<IProjectActions>(), ImmediateDispatcher.Instance);
    }

    public TestFolder Folder { get; } = new();

    public FakeSettingsService Settings { get; } = new();

    public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

    public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

    public INavigationService Navigation { get; } = Substitute.For<INavigationService>();

    public IShellIntegration Shell { get; } = Substitute.For<IShellIntegration>();

    public IGitService Git { get; } = Substitute.For<IGitService>();

    public IGitHubAccountService Accounts { get; } = Substitute.For<IGitHubAccountService>();

    public IGitHubService GitHub { get; } = Substitute.For<IGitHubService>();

    public IShellDiscovery Shells { get; } = Substitute.For<IShellDiscovery>();

    public IActivityLog Activity { get; } = Substitute.For<IActivityLog>();

    public IUpdateService Updates { get; } = Substitute.For<IUpdateService>();

    public AppPaths Paths { get; }

    public Database Database { get; }

    public StatusBarViewModel StatusBar { get; }

    internal SettingsStore CreateStore() =>
        new(Settings, Notifications, ImmediateDispatcher.Instance, NullLogger.Instance, TimeSpan.Zero);

    public UpdateCoordinator CreateCoordinator(TimeProvider? time = null) =>
        new(Updates, Settings, StatusBar, Notifications, ImmediateDispatcher.Instance, NullLogger<UpdateCoordinator>.Instance, time ?? TimeProvider.System);

    public SettingsViewModel CreatePage() =>
        new(Settings, Dialogs, Notifications, Navigation, Shell, Git, Accounts, GitHub, Shells, Paths, Database, Activity, Updates, CreateCoordinator(),
            ImmediateDispatcher.Instance, NullLogger<SettingsViewModel>.Instance, TimeSpan.Zero);

    public void Dispose()
    {
        StatusBar.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Folder.Dispose();
    }
}
