using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Commands;

/// <summary>
/// The selected run: its header (label, command line, directory, timing, status), the actions
/// that apply to it (cancel, run again, open the log file, copy), the stalled-run warning and
/// its output.
/// </summary>
public sealed partial class RunDetailViewModel : ViewModelBase, IDisposable
{
    private readonly CommandsSectionViewModel _owner;
    private readonly CancellationTokenSource _loading = new();
    private bool _disposed;

    internal RunDetailViewModel(CommandsSectionViewModel owner, RunItemViewModel run, TimeSpan batchInterval)
    {
        _owner = owner;
        Run = run;
        Log = new RunLogViewModel(owner.Services.Dispatcher, batchInterval);
        Run.PropertyChanged += OnRunPropertyChanged;
    }

    public RunItemViewModel Run { get; }

    public RunLogViewModel Log { get; }

    /// <summary>No output for longer than the "stalled" threshold while running.</summary>
    [ObservableProperty]
    public partial bool IsStalled { get; private set; }

    [ObservableProperty]
    public partial string StalledMessage { get; private set; } = string.Empty;

    /// <summary>Working directory shown relative to the project when it is inside it.</summary>
    public string WorkingDirectoryText
    {
        get
        {
            var root = _owner.Context.Root;
            var directory = Run.WorkingDirectory;
            if (directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var relative = directory[root.Length..].Trim('\\', '/').Replace('\\', '/');
                return relative.Length == 0 ? "Project root" : relative;
            }

            return directory;
        }
    }

    /// <summary>Shows the output: follows the live session, or reads the log file of a finished run.</summary>
    public async Task LoadAsync()
    {
        if (Run.Session is { } session && Run.IsRunning)
        {
            Log.Attach(session);
            UpdateStalled(_owner.Now);
            return;
        }

        await LoadFinishedOutputAsync().ConfigureAwait(true);
    }

    /// <summary>The run finished while shown: keep its output and show the error summary.</summary>
    internal void OnCompleted()
    {
        Log.Complete();
        IsStalled = false;
        CancelCommand.NotifyCanExecuteChanged();
    }

    internal void Tick(DateTimeOffset now) => UpdateStalled(now);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync() => RunAsync(() => _owner.Services.Runs.CancelAsync(Run.Id), errorTitle: $"Could not stop {Run.Label}",
        errorMode: ErrorMode.Toast, notifications: _owner.Services.Notifications);

    private bool CanCancel() => Run.IsRunning;

    [RelayCommand]
    private Task RunAgainAsync() => _owner.StartAsync(Run.Request);

    [RelayCommand]
    private async Task OpenLogFileAsync()
    {
        await RunAsync(async () =>
        {
            var record = Run.Record ?? await _owner.Services.Runs.GetAsync(Run.Id, _owner.Context.Lifetime).ConfigureAwait(true);
            if (record is null || !File.Exists(record.LogPath))
            {
                throw new ForgeException(ErrorKind.NotFound, "The log file of this run no longer exists.",
                    "Old logs are removed when the run history is trimmed.");
            }

            _owner.Services.Shell.OpenWithDefaultApp(record.LogPath);
        }, errorTitle: "Could not open the log file", errorMode: ErrorMode.Toast, notifications: _owner.Services.Notifications).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (Log.Lines.Count == 0)
        {
            return;
        }

        Copy(Log.Text, "Output copied", Format.Count(Log.Lines.Count, "line"));
    }

    [RelayCommand]
    private void CopyCommandLine() => Copy(Run.CommandLine, "Command copied", Run.CommandLine);

    [RelayCommand]
    private void CopyErrorSummary()
    {
        if (Run.ErrorSummary is { Length: > 0 } summary)
        {
            Copy(summary, "Error summary copied", null);
        }
    }

    [RelayCommand]
    private Task RetryLoadAsync() => LoadFinishedOutputAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Run.PropertyChanged -= OnRunPropertyChanged;
        _loading.Cancel();
        _loading.Dispose();
        Log.Dispose();
    }

    private Task LoadFinishedOutputAsync()
    {
        var token = _loading.Token;
        return RunAsync(
            () => Log.LoadAsync(ct => _owner.Services.Runs.ReadLogAsync(Run.Id, cancellationToken: ct), token),
            "Reading the output…",
            "Could not read the output of this run");
    }

    private void Copy(string text, string title, string? message)
    {
        try
        {
            _owner.Services.Shell.CopyToClipboard(text);
            _owner.Services.Notifications.Show(title, message, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            _owner.Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    private void UpdateStalled(DateTimeOffset now)
    {
        var minutes = _owner.Services.Settings.Current.StalledRunWarningMinutes;
        if (minutes <= 0 || !Run.IsRunning || Run.Session is not { } session)
        {
            IsStalled = false;
            return;
        }

        var quiet = now - session.LastOutputAt;
        IsStalled = quiet >= TimeSpan.FromMinutes(minutes);
        if (IsStalled)
        {
            var shown = Math.Max(minutes, (int)quiet.TotalMinutes);
            StalledMessage = $"No output for {Format.Count(shown, "minute")} — the command may be waiting for input or stuck.";
        }
    }

    private void OnRunPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunItemViewModel.Status))
        {
            CancelCommand.NotifyCanExecuteChanged();
        }
    }
}
