using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Services.Windowing;

/// <summary>
/// Mirrors running commands on the taskbar button: indeterminate while commands run (or their
/// average progress when every run reports one), and a brief red state when a run fails.
/// </summary>
internal sealed class TaskbarProgressService : IDisposable
{
    private static readonly TimeSpan ErrorFlashDuration = TimeSpan.FromSeconds(3);

    private readonly IRunService _runs;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, IRunSession> _tracked = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private TaskbarItemInfo? _taskbar;
    private DispatcherTimer? _errorTimer;

    public TaskbarProgressService(IRunService runs, IUiDispatcher dispatcher)
    {
        _runs = runs;
        _dispatcher = dispatcher;
    }

    /// <summary>Starts reflecting runs on <paramref name="window"/>'s taskbar button.</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _taskbar = window.TaskbarItemInfo ??= new TaskbarItemInfo();
        _errorTimer = new DispatcherTimer { Interval = ErrorFlashDuration };
        _errorTimer.Tick += (_, _) =>
        {
            _errorTimer.Stop();
            Update();
        };

        _runs.RunStarted += OnRunStarted;
        _runs.RunCompleted += OnRunCompleted;
        foreach (var session in _runs.ActiveRuns)
        {
            Track(session);
        }

        Update();
    }

    public void Dispose()
    {
        _runs.RunStarted -= OnRunStarted;
        _runs.RunCompleted -= OnRunCompleted;
        lock (_gate)
        {
            foreach (var session in _tracked.Values)
            {
                session.ProgressChanged -= OnProgressChanged;
            }

            _tracked.Clear();
        }

        _errorTimer?.Stop();
    }

    private void OnRunStarted(object? sender, IRunSession session)
    {
        Track(session);
        _dispatcher.Post(Update);
    }

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e)
    {
        lock (_gate)
        {
            if (_tracked.Remove(e.Record.Id, out var session))
            {
                session.ProgressChanged -= OnProgressChanged;
            }
        }

        _dispatcher.Post(() =>
        {
            if (e.Record.Status == RunStatus.Failed)
            {
                FlashError();
            }
            else
            {
                Update();
            }
        });
    }

    private void OnProgressChanged(object? sender, EventArgs e) => _dispatcher.Post(Update);

    private void Track(IRunSession session)
    {
        lock (_gate)
        {
            if (_tracked.TryAdd(session.Id, session))
            {
                session.ProgressChanged += OnProgressChanged;
            }
        }
    }

    private void FlashError()
    {
        if (_taskbar is null || _errorTimer is null)
        {
            return;
        }

        _taskbar.ProgressState = TaskbarItemProgressState.Error;
        _taskbar.ProgressValue = 1;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    private void Update()
    {
        if (_taskbar is null || _errorTimer is { IsEnabled: true })
        {
            return;
        }

        var active = _runs.ActiveRuns;
        if (active.Count == 0)
        {
            _taskbar.ProgressState = TaskbarItemProgressState.None;
            return;
        }

        var progress = active.Select(run => run.Progress).ToList();
        if (progress.All(p => p is not null))
        {
            _taskbar.ProgressState = TaskbarItemProgressState.Normal;
            _taskbar.ProgressValue = Math.Clamp(progress.Average(p => p!.Value), 0, 1);
        }
        else
        {
            _taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
        }
    }
}
