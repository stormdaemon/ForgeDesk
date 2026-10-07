using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Settings.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Dashboard;

public sealed class CloneRepositoryFlowTests : IDisposable
{
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IProjectCloneService _clones = Substitute.For<IProjectCloneService>();
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_successful_clone_opens_the_new_project()
    {
        var project = TestData.Project("repo", _folder.Combine("repo"));
        _clones.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>>(), Arg.Any<CancellationToken>()).Returns(project);
        _dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns(async call =>
        {
            var dialog = (CloneRepositoryDialogViewModel)call.Arg<IDialogViewModel>();
            bool? result = null;
            dialog.CloseRequested += (_, r) => result = r;
            dialog.Url = "https://github.com/owner/repo.git";
            await dialog.CloneCommand.ExecuteAsync(null);
            return result;
        });

        await CreateFlow().RequestCloneAsync();

        await _navigation.Received(1).OpenProjectAsync(project.Id, null, null);
        _notifications.Received(1).Show("Cloned repo", project.Path, NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task A_cancelled_dialog_opens_nothing()
    {
        _dialogs.ShowDialogAsync(Arg.Any<IDialogViewModel>()).Returns((bool?)null);

        var project = await CreateFlow().CloneAsync();

        project.Should().BeNull();
        await _navigation.DidNotReceiveWithAnyArgs().OpenProjectAsync(default!, default, default);
    }

    [Fact]
    public void The_flow_resolves_from_a_container_without_github_services()
    {
        // Registering the flow must not force GitHub or git services into existence (it enables the
        // shell's clone entry points even in minimal hosts).
        var services = new ServiceCollection();
        services.AddSingleton(_dialogs);
        services.AddSingleton(_navigation);
        services.AddSingleton(_notifications);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<CloneRepositoryFlow>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        provider.GetRequiredService<CloneRepositoryFlow>().Should().NotBeNull();
    }

    private CloneRepositoryFlow CreateFlow()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { DefaultCloneDirectory = _folder.Path });
        var registry = Substitute.For<IProjectRegistry>();
        registry.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Project>());
        return new CloneRepositoryFlow(Substitute.For<IServiceProvider>(), _dialogs, _navigation, _notifications, NullLogger<CloneRepositoryFlow>.Instance,
            () => new CloneRepositoryDialogViewModel(Substitute.For<IGitHubService>(), Substitute.For<IGitHubAccountService>(), _clones, registry, settings,
                _dialogs, _navigation, Substitute.For<IShellIntegration>(), ImmediateDispatcher.Instance, NullLogger<CloneRepositoryDialogViewModel>.Instance));
    }
}
