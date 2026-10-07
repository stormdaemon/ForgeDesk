using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Storage;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Settings;

/// <summary>A database backup file.</summary>
public sealed record BackupItem(string Path, string FileName, DateTimeOffset CreatedAt, long Size)
{
    public string SizeText => Format.Bytes(Size);
}

/// <summary>Settings › Data &amp; storage: where ForgeDesk keeps its data, backups, clearing and resetting.</summary>
public sealed partial class DataSettingsViewModel : SettingsSectionViewModel
{
    public const int ShownBackups = 5;

    private readonly IAppPaths _paths;
    private readonly Database _database;
    private readonly IActivityLog _activity;
    private readonly IGitHubAccountService _accounts;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IShellIntegration _shell;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    internal DataSettingsViewModel(
        IAppPaths paths,
        Database database,
        IActivityLog activity,
        IGitHubAccountService accounts,
        IDialogService dialogs,
        INotificationService notifications,
        IShellIntegration shell,
        ILogger logger,
        TimeProvider? time = null)
        : base("Data", "Data & storage", "Database20", "Data folder, backups and reset", "data storage folder database backup logs activity clear reset delete")
    {
        _paths = paths;
        _database = database;
        _activity = activity;
        _accounts = accounts;
        _dialogs = dialogs;
        _notifications = notifications;
        _shell = shell;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public string DataFolder => _paths.DataDirectory;

    public string LogsFolder => _paths.LogsDirectory;

    public string BackupsFolder => _paths.BackupsDirectory;

    public ObservableCollection<BackupItem> Backups { get; } = [];

    [ObservableProperty]
    public partial bool HasBackups { get; private set; }

    [ObservableProperty]
    public partial bool IsBackingUp { get; private set; }

    [ObservableProperty]
    public partial bool IsClearingActivity { get; private set; }

    /// <summary>A reset was requested and runs at the next start.</summary>
    [ObservableProperty]
    public partial bool IsResetPending { get; private set; }

    protected override Task LoadAsync()
    {
        RefreshBackups();
        IsResetPending = SafeIsResetPending();
        return Task.CompletedTask;
    }

    protected override Task OnReactivatedAsync() => LoadAsync();

    [RelayCommand]
    private void OpenDataFolder() => Open(DataFolder);

    [RelayCommand]
    private void OpenLogsFolder() => Open(LogsFolder);

    [RelayCommand]
    private void OpenBackupsFolder() => Open(BackupsFolder);

    [RelayCommand]
    private void RevealBackup(BackupItem? backup)
    {
        if (backup is null)
        {
            return;
        }

        try
        {
            _shell.RevealInExplorer(backup.Path);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not show the backup"));
        }
    }

    [RelayCommand]
    private async Task BackUpNowAsync()
    {
        IsBackingUp = true;
        try
        {
            var path = await _database.BackupAsync().ConfigureAwait(true);
            RefreshBackups();
            _notifications.Show("Backup created", path, NotificationSeverity.Success, new NotificationAction("Show", () =>
            {
                _shell.RevealInExplorer(path);
                return Task.CompletedTask;
            }));
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Manual backup failed");
            _notifications.ShowError(ErrorInfo.From(ex, "Could not back up ForgeDesk's data"));
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    [RelayCommand]
    private async Task ClearActivityAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = "Clear the activity history?",
            Message = "ForgeDesk forgets the journal of what happened in every project (commits, runs, pushes…). "
                + "Your projects, tasks and git history are not affected.",
            ConfirmText = "Clear activity",
            IsDestructive = true,
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        IsClearingActivity = true;
        try
        {
            await _activity.ClearAsync(null).ConfigureAwait(true);
            _notifications.Show("Activity history cleared", null, NotificationSeverity.Success);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not clear the activity history"));
        }
        finally
        {
            IsClearingActivity = false;
        }
    }

    /// <summary>Two confirmations, then ForgeDesk's own data is deleted at the next start (never project files).</summary>
    [RelayCommand]
    private async Task ResetAsync()
    {
        var first = await _dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = "Reset ForgeDesk?",
            Message = "This deletes ForgeDesk's own data: the list of projects, tasks, run history, activity, cached statuses and all settings, "
                + "and signs you out of GitHub.\n\nYour project folders and their files are NOT touched. Backups are kept in "
                + $"{BackupsFolder}.",
            ConfirmText = "Continue",
            IsDestructive = true,
        }).ConfigureAwait(true);
        if (!first)
        {
            return;
        }

        var second = await _dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = "Are you sure?",
            Message = "ForgeDesk will start like a fresh install the next time it opens. This can't be undone from ForgeDesk.",
            ConfirmText = "Reset ForgeDesk",
            IsDestructive = true,
        }).ConfigureAwait(true);
        if (!second)
        {
            return;
        }

        try
        {
            DataReset.Schedule(_paths, _time.GetLocalNow());
            IsResetPending = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not prepare the reset"));
            return;
        }

        try
        {
            await _accounts.SignOutAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not sign out while resetting");
        }

        // Informational: both buttons (and Esc) just close it; the reset stays scheduled.
        await _dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = "Restart ForgeDesk to finish",
            Message = "Close ForgeDesk and open it again: the reset happens as it starts. "
                + "Until then you can still cancel it in Settings › Data & storage.",
            ConfirmText = "Got it",
            CancelText = "Close",
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelReset()
    {
        try
        {
            DataReset.Cancel(_paths);
            IsResetPending = false;
            _notifications.Show("Reset cancelled", "ForgeDesk keeps its data.", NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not cancel the reset"));
        }
    }

    private void RefreshBackups()
    {
        try
        {
            var backups = _database.ListBackups()
                .Take(ShownBackups)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .Select(file => new BackupItem(file.FullName, file.Name, new DateTimeOffset(file.LastWriteTime), file.Length))
                .ToList();
            Backups.Clear();
            foreach (var backup in backups)
            {
                Backups.Add(backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not list the backups");
        }

        HasBackups = Backups.Count > 0;
    }

    private bool SafeIsResetPending()
    {
        try
        {
            return DataReset.IsPending(_paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _shell.OpenFolder(folder);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the folder"));
        }
    }
}
