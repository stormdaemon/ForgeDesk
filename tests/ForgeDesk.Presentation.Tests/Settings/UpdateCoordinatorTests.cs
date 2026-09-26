using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Tests.Settings.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class UpdateCoordinatorTests : IDisposable
{
    private readonly SettingsHarness _harness = new();

    public UpdateCoordinatorTests()
    {
        _harness.Updates.IsSupported.Returns(true);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task An_available_update_lights_up_the_status_bar_and_offers_to_restart()
    {
        _harness.Updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(new UpdateCheckResult(true, "1.5.0", "- Faster dashboard"));
        NotificationAction? action = null;
        _harness.Notifications.Show("ForgeDesk 1.5.0 is available", Arg.Any<string?>(), NotificationSeverity.Info, Arg.Do<NotificationAction?>(a => action = a));
        using var updates = _harness.CreateCoordinator();

        await updates.CheckAutomaticallyAsync();

        updates.Status.Should().Be(UpdateStatus.Available);
        updates.HasUpdate.Should().BeTrue();
        updates.ReleaseNotes.Should().Be("- Faster dashboard");
        _harness.StatusBar.UpdateAvailableVersion.Should().Be("1.5.0");
        _harness.StatusBar.UpdateText.Should().Be("Update 1.5.0 available");
        action!.Label.Should().Be("Restart to update");

        await action.Execute();

        await _harness.Updates.Received(1).DownloadAndApplyAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Being_up_to_date_clears_the_badge()
    {
        _harness.StatusBar.UpdateAvailableVersion = "1.3.0";
        _harness.Updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(UpdateCheckResult.UpToDate);
        using var updates = _harness.CreateCoordinator();

        await updates.CheckAsync();

        updates.Status.Should().Be(UpdateStatus.UpToDate);
        updates.StatusText.Should().Be("You're up to date.");
        updates.LastCheckedAt.Should().NotBeNull();
        _harness.StatusBar.UpdateAvailableVersion.Should().BeNull();
    }

    [Fact]
    public async Task The_automatic_check_fails_silently()
    {
        _harness.Updates.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UpdateCheckResult>(new ForgeException(ErrorKind.NetworkUnavailable, "GitHub can't be reached.")));
        using var updates = _harness.CreateCoordinator();

        await updates.CheckAutomaticallyAsync();

        updates.Status.Should().Be(UpdateStatus.Failed);
        updates.LastError!.Message.Should().Be("GitHub can't be reached.");
        _harness.Notifications.DidNotReceiveWithAnyArgs().ShowError(default!, default);
        _harness.Notifications.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task The_automatic_check_respects_the_setting()
    {
        _harness.Settings.ChangeExternally(s => s with { CheckForUpdatesAutomatically = false });
        using var updates = _harness.CreateCoordinator();

        await updates.CheckAutomaticallyAsync();

        await _harness.Updates.DidNotReceiveWithAnyArgs().CheckAsync(default);
        updates.Status.Should().Be(UpdateStatus.Idle);
    }

    [Fact]
    public async Task Copies_that_cannot_update_never_check()
    {
        _harness.Updates.IsSupported.Returns(false);
        using var updates = _harness.CreateCoordinator();

        await updates.CheckAutomaticallyAsync();
        var result = await updates.CheckAsync();

        result.Should().BeNull();
        updates.Status.Should().Be(UpdateStatus.Unsupported);
        updates.StatusText.Should().Contain("doesn't update itself");
        await _harness.Updates.DidNotReceiveWithAnyArgs().CheckAsync(default);
    }

    [Fact]
    public async Task Download_progress_is_shown_and_failures_reported()
    {
        _harness.Updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(new UpdateCheckResult(true, "1.5.0", null));
        var seen = new List<double>();
        using var updates = _harness.CreateCoordinator();
        _harness.Updates.DownloadAndApplyAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var progress = call.Arg<IProgress<double>?>()!;
            progress.Report(0.5);
            seen.Add(updates.DownloadProgress);
            updates.IsDownloading.Should().BeTrue();
            return Task.FromException(new ForgeException(ErrorKind.NetworkUnavailable, "The update could not be downloaded."));
        });
        await updates.CheckAsync();

        await updates.DownloadAndRestartAsync();

        seen.Should().Equal(0.5);
        updates.Status.Should().Be(UpdateStatus.Failed);
        _harness.Notifications.Received(1).ShowError(Arg.Is<ErrorInfo>(e => e.Title == "Could not install the update"), Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Installing_without_a_known_update_checks_first()
    {
        _harness.Updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(UpdateCheckResult.UpToDate);
        using var updates = _harness.CreateCoordinator();

        await updates.DownloadAndRestartAsync();

        _harness.Notifications.Received(1).Show("ForgeDesk is up to date", "You have the latest version (1.4.0).", NotificationSeverity.Success,
            Arg.Any<NotificationAction?>());
        await _harness.Updates.DidNotReceiveWithAnyArgs().DownloadAndApplyAsync(default, default);
    }

    [Fact]
    public async Task The_updates_section_checks_and_saves_its_switch()
    {
        _harness.Updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(UpdateCheckResult.UpToDate);
        using var coordinator = _harness.CreateCoordinator();
        var section = new UpdatesSettingsViewModel(_harness.CreateStore(), coordinator);

        await section.CheckForUpdatesCommand.ExecuteAsync(null);
        section.CheckAutomatically = false;

        coordinator.Status.Should().Be(UpdateStatus.UpToDate);
        _harness.Settings.Current.CheckForUpdatesAutomatically.Should().BeFalse();
    }

    [Fact]
    public void The_placeholder_updater_reports_the_product_version()
    {
        var updater = new UnavailableUpdateService();

        updater.IsSupported.Should().BeFalse();
        updater.CurrentVersion.Should().NotBeNullOrWhiteSpace().And.NotContain("+");
    }

    [Fact]
    public async Task The_palette_lists_every_settings_section_and_the_update_check()
    {
        var pages = Substitute.For<IPageFactory>();
        pages.IsAvailable(PageKind.Settings).Returns(true);
        _harness.Updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(UpdateCheckResult.UpToDate);
        using var updates = _harness.CreateCoordinator();
        var source = new SettingsPaletteSource(_harness.Navigation, pages, updates);

        var items = await source.GetItemsAsync(new PaletteQuery(string.Empty, null), CancellationToken.None);

        items.Where(i => i.Category == PaletteCategory.Setting).Should().HaveCount(10);
        var github = items.Single(i => i.Title == "Settings: GitHub");
        await github.Execute();
        _harness.Navigation.Received(1).OpenSettings("GitHub");

        var check = items.Single(i => i.Title == "Check for updates");
        await check.Execute();
        _harness.Navigation.Received(1).OpenSettings("Updates");
        await _harness.Updates.Received(1).CheckAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_palette_is_empty_without_the_settings_page()
    {
        var pages = Substitute.For<IPageFactory>();
        using var updates = _harness.CreateCoordinator();

        var items = await new SettingsPaletteSource(_harness.Navigation, pages, updates).GetItemsAsync(new PaletteQuery("git", null), CancellationToken.None);

        items.Should().BeEmpty();
    }
}
