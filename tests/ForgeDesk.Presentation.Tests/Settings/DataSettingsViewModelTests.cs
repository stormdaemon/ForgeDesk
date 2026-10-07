using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Tests.Settings.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class DataSettingsViewModelTests : IDisposable
{
    private readonly SettingsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task It_shows_where_the_data_lives()
    {
        var data = Create();

        await data.ActivateAsync();

        data.DataFolder.Should().Be(_harness.Paths.DataDirectory);
        data.LogsFolder.Should().Be(_harness.Paths.LogsDirectory);
        data.HasBackups.Should().BeFalse();

        data.OpenDataFolderCommand.Execute(null);
        data.OpenLogsFolderCommand.Execute(null);

        _harness.Shell.Received(1).OpenFolder(_harness.Paths.DataDirectory);
        _harness.Shell.Received(1).OpenFolder(_harness.Paths.LogsDirectory);
    }

    [Fact]
    public async Task Back_up_now_writes_a_backup_and_lists_it()
    {
        await _harness.Database.InitializeAsync();
        var data = Create();
        await data.ActivateAsync();

        await data.BackUpNowCommand.ExecuteAsync(null);

        data.HasBackups.Should().BeTrue();
        data.Backups.Should().ContainSingle();
        File.Exists(data.Backups[0].Path).Should().BeTrue();
        data.Backups[0].FileName.Should().StartWith("forgedesk-");
        _harness.Notifications.Received(1).Show("Backup created", data.Backups[0].Path, NotificationSeverity.Success, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task Clearing_the_activity_asks_first()
    {
        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(false, true);
        var data = Create();

        await data.ClearActivityCommand.ExecuteAsync(null);
        await _harness.Activity.DidNotReceiveWithAnyArgs().ClearAsync(default, default);

        await data.ClearActivityCommand.ExecuteAsync(null);

        await _harness.Activity.Received(1).ClearAsync(null, Arg.Any<CancellationToken>());
        await _harness.Dialogs.Received(2).ConfirmAsync(Arg.Is<ConfirmOptions>(o => o.IsDestructive));
    }

    [Fact]
    public async Task Reset_needs_two_confirmations_then_schedules_the_reset_and_signs_out()
    {
        var confirmations = new List<ConfirmOptions>();
        _harness.Dialogs.ConfirmAsync(Arg.Do<ConfirmOptions>(confirmations.Add)).Returns(true);
        var data = Create();

        await data.ResetCommand.ExecuteAsync(null);

        confirmations.Should().HaveCount(3);
        confirmations[0].IsDestructive.Should().BeTrue();
        confirmations[0].Message.Should().Contain("NOT touched");
        confirmations[1].IsDestructive.Should().BeTrue();
        confirmations[2].Title.Should().Contain("Restart");
        DataReset.IsPending(_harness.Paths).Should().BeTrue();
        data.IsResetPending.Should().BeTrue();
        await _harness.Accounts.Received(1).SignOutAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_second_confirmation_changes_nothing()
    {
        _harness.Dialogs.ConfirmAsync(Arg.Any<ConfirmOptions>()).Returns(true, false);
        var data = Create();

        await data.ResetCommand.ExecuteAsync(null);

        DataReset.IsPending(_harness.Paths).Should().BeFalse();
        await _harness.Accounts.DidNotReceive().SignOutAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_scheduled_reset_can_be_cancelled()
    {
        DataReset.Schedule(_harness.Paths, DateTimeOffset.Now);
        var data = Create();
        await data.ActivateAsync();
        data.IsResetPending.Should().BeTrue();

        data.CancelResetCommand.Execute(null);

        data.IsResetPending.Should().BeFalse();
        DataReset.IsPending(_harness.Paths).Should().BeFalse();
    }

    private DataSettingsViewModel Create() => new(_harness.Paths, _harness.Database, _harness.Activity, _harness.Accounts, _harness.Dialogs,
        _harness.Notifications, _harness.Shell, NullLogger.Instance);
}

public sealed class DataResetTests : IDisposable
{
    private readonly Workspace.Support.TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Nothing_happens_without_a_request()
    {
        var paths = Paths();
        File.WriteAllText(paths.DatabasePath, "db");

        DataReset.ApplyPending(paths).Should().BeFalse();

        File.Exists(paths.DatabasePath).Should().BeTrue();
    }

    [Fact]
    public void A_requested_reset_deletes_the_database_run_logs_and_cache_but_keeps_backups_and_logs()
    {
        var paths = Paths();
        File.WriteAllText(paths.DatabasePath, "db");
        File.WriteAllText(paths.DatabasePath + "-wal", "wal");
        File.WriteAllText(paths.DatabasePath + "-shm", "shm");
        File.WriteAllText(Path.Combine(paths.RunLogsDirectory, "run.log"), "log");
        Directory.CreateDirectory(Path.Combine(paths.CacheDirectory, "avatars"));
        File.WriteAllText(Path.Combine(paths.BackupsDirectory, "forgedesk-20260901-120000.db"), "backup");
        File.WriteAllText(Path.Combine(paths.LogsDirectory, "forgedesk.log"), "app log");
        DataReset.Schedule(paths, DateTimeOffset.Now);

        DataReset.ApplyPending(paths).Should().BeTrue();

        File.Exists(paths.DatabasePath).Should().BeFalse();
        File.Exists(paths.DatabasePath + "-wal").Should().BeFalse();
        File.Exists(paths.DatabasePath + "-shm").Should().BeFalse();
        Directory.EnumerateFileSystemEntries(paths.RunLogsDirectory).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(paths.CacheDirectory).Should().BeEmpty();
        File.Exists(Path.Combine(paths.BackupsDirectory, "forgedesk-20260901-120000.db")).Should().BeTrue();
        File.Exists(Path.Combine(paths.LogsDirectory, "forgedesk.log")).Should().BeTrue();
        DataReset.IsPending(paths).Should().BeFalse("the request is done");
    }

    private AppPaths Paths()
    {
        var paths = new AppPaths(_folder.Combine("data"));
        paths.EnsureCreated();
        return paths;
    }
}
