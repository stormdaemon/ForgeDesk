using ForgeDesk.Core.Common;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Shell;

public sealed class ProjectActionsTests
{
    private const string Folder = @"C:\dev\forge-app";

    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IShellIntegration _shell = Substitute.For<IShellIntegration>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly Project _project = TestData.Project("forge-app", Folder);

    public ProjectActionsTests()
    {
        _settings.Current.Returns(AppSettings.Default with { DefaultCloneDirectory = @"C:\dev" });
        _registry.GetAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(_project);
    }

    [Fact]
    public async Task Adding_a_folder_registers_and_opens_it()
    {
        _dialogs.PickFolderAsync(Arg.Any<string>(), @"C:\dev").Returns(Folder);
        _registry.AddAsync(Folder, null, Arg.Any<CancellationToken>()).Returns(_project);

        await Create().AddLocalProjectAsync();

        await _navigation.Received(1).OpenProjectAsync(_project.Id, null, null);
        _notifications.Received(1).Show("Added forge-app", Folder, NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Adding_an_already_registered_folder_just_opens_it()
    {
        _registry.FindByPathAsync(Folder, Arg.Any<CancellationToken>()).Returns(_project);

        var opened = await Create().AddOrOpenFolderAsync(Folder);

        opened.Should().BeTrue();
        await _registry.DidNotReceiveWithAnyArgs().AddAsync(default!, default, default);
        await _navigation.Received(1).OpenProjectAsync(_project.Id, null, null);
    }

    [Fact]
    public async Task Already_exists_under_another_spelling_opens_the_existing_project()
    {
        _registry.FindByPathAsync(Folder, Arg.Any<CancellationToken>()).Returns(null, _project);
        _registry.AddAsync(Folder, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Project>(new ForgeException(ErrorKind.AlreadyExists, "This folder is already in ForgeDesk.")));

        var opened = await Create().AddOrOpenFolderAsync(Folder);

        opened.Should().BeTrue();
        await _navigation.Received(1).OpenProjectAsync(_project.Id, null, null);
        _notifications.DidNotReceiveWithAnyArgs().ShowError(default!, default);
    }

    [Fact]
    public async Task Cancelling_the_folder_picker_does_nothing()
    {
        _dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns((string?)null);

        await Create().AddLocalProjectAsync();

        await _registry.DidNotReceiveWithAnyArgs().AddAsync(default!, default, default);
        _notifications.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_that_cannot_be_added_is_reported()
    {
        _registry.AddAsync(Folder, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Project>(new ForgeException(ErrorKind.PathNotFound, "The folder does not exist.")));

        var opened = await Create().AddOrOpenFolderAsync(Folder);

        opened.Should().BeFalse();
        _notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Kind == ErrorKind.PathNotFound && e.Title == "Could not add the project"),
            Arg.Any<NotificationAction?>());
        await _navigation.DidNotReceiveWithAnyArgs().OpenProjectAsync(default!, default, default);
    }

    [Fact]
    public async Task Opening_failure_is_reported_and_returns_false()
    {
        _navigation.OpenProjectAsync("gone", null, null)
            .Returns(Task.FromException(new ForgeException(ErrorKind.NotFound, "This project is no longer registered in ForgeDesk.")));

        var opened = await Create().OpenAsync("gone");

        opened.Should().BeFalse();
        _notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Kind == ErrorKind.NotFound), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Removing_asks_first_and_says_files_are_kept()
    {
        ConfirmOptions? asked = null;
        _dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(o => asked = o)).Returns(true);

        var removed = await Create().RemoveAsync(_project.Id);

        removed.Should().BeTrue();
        asked!.IsDestructive.Should().BeTrue();
        asked.ConfirmText.Should().Be("Remove from ForgeDesk");
        asked.Message.Should().Contain(Folder).And.Contain("stay on disk");
        Received.InOrder(() =>
        {
            _registry.RemoveAsync(_project.Id, Arg.Any<CancellationToken>());
            _navigation.CloseProject(_project.Id);
        });
    }

    [Fact]
    public async Task Declining_removal_keeps_the_project()
    {
        _dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false);

        var removed = await Create().RemoveAsync(_project.Id);

        removed.Should().BeFalse();
        await _registry.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
        _navigation.DidNotReceiveWithAnyArgs().CloseProject(default!);
    }

    [Fact]
    public async Task Pin_toggles_and_rename_trims_the_new_name()
    {
        _dialogs.PromptAsync(Arg.Any<PromptOptions>()).Returns("  Forge App  ");
        var actions = Create();

        await actions.TogglePinAsync(_project.Id);
        await actions.RenameAsync(_project.Id);

        await _registry.Received(1).UpdateAsync(Arg.Is<Project>(p => p.IsPinned), Arg.Any<CancellationToken>());
        await _registry.Received(1).UpdateAsync(Arg.Is<Project>(p => p.Name == "Forge App" && !p.IsPinned), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("ok", null)]
    public void Project_names_are_validated(string name, string? error) => ProjectActions.ValidateName(name).Should().Be(error);

    [Fact]
    public async Task Clone_is_available_only_with_a_handler()
    {
        Create().CanClone.Should().BeFalse();
        await Create().CloneRepositoryAsync();

        var handler = Substitute.For<ICloneRequestHandler>();
        var withHandler = Create(handler);

        withHandler.CanClone.Should().BeTrue();
        await withHandler.CloneRepositoryAsync();
        await handler.Received(1).RequestCloneAsync();
    }

    [Fact]
    public void Explorer_failures_are_reported()
    {
        _shell.When(s => s.OpenFolder(Folder)).Do(_ => throw new ForgeException(ErrorKind.PathNotFound, "The folder does not exist."));

        Create().OpenInExplorer(Folder);

        _notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Kind == ErrorKind.PathNotFound), Arg.Any<NotificationAction?>());
    }

    private ProjectActions Create(params ICloneRequestHandler[] cloneHandlers) =>
        new(_registry, _navigation, _dialogs, _notifications, _shell, _settings, cloneHandlers, NullLogger<ProjectActions>.Instance);
}
