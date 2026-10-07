using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Terminal;

public enum TerminalSessionState
{
    /// <summary>Waiting for the terminal control, or the shell is being started.</summary>
    Starting,
    Running,

    /// <summary>The shell process ended (see <see cref="TerminalSessionViewModel.ExitCode"/>).</summary>
    Exited,

    /// <summary>The shell could not be started (see <see cref="TerminalSessionViewModel.ErrorMessage"/>).</summary>
    Failed,
}

/// <summary>What the app needs to start one shell process in a pseudo console.</summary>
public sealed record TerminalLaunch(int Generation, ShellProfile Profile, string WorkingDirectory)
{
    public string CommandLine => Profile.CommandLine;
}

/// <summary>
/// The app side of a terminal session: a terminal control connected to a ConPTY running the shell.
/// Implemented by the WPF app (one per session, kept alive across tab and project switches) and
/// reports back through <see cref="TerminalSessionViewModel.ReportStarted"/>,
/// <see cref="TerminalSessionViewModel.ReportExited"/> and <see cref="TerminalSessionViewModel.ReportFailed"/>
/// with the launch's generation (so reports of a replaced process are ignored).
/// </summary>
public interface ITerminalBackend
{
    /// <summary>Starts the shell (after stopping any previous process of the session).</summary>
    void Start(TerminalLaunch launch);

    /// <summary>Kills the shell and its child processes.</summary>
    void Stop();

    /// <summary>Clears the screen and scrollback, then sends <paramref name="shellInput"/> so the shell redraws its prompt.</summary>
    void Clear(string? shellInput);

    /// <summary>Moves the keyboard focus into the terminal.</summary>
    void Focus();
}

/// <summary>
/// One integrated terminal: a shell profile started in a folder of the project, its live state
/// (starting, running, exited with a code, failed to start) and the commands acting on it.
/// Sessions live as long as the project workspace: switching tabs or projects never kills them.
/// </summary>
public sealed partial class TerminalSessionViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly string _projectRoot;
    private readonly string _projectName;
    private ITerminalBackend? _backend;
    private bool _launched;
    private bool _closed;

    public TerminalSessionViewModel(int id, string title, ShellProfile profile, string workingDirectory, string projectRoot, string projectName, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Id = id;
        Title = title;
        Profile = profile;
        WorkingDirectory = workingDirectory;
        _projectRoot = projectRoot;
        _projectName = projectName;
        _dispatcher = dispatcher;
    }

    public int Id { get; }

    /// <summary>Stable automation id of the session tab ("Terminal.Session.2").</summary>
    public string AutomationId => $"Terminal.Session.{Id}";

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip), nameof(ClearInput))]
    public partial ShellProfile Profile { get; private set; }

    /// <summary>Full path of the folder the shell starts in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectoryLabel), nameof(ToolTip))]
    public partial string WorkingDirectory { get; private set; }

    /// <summary>"forge-app" or "forge-app/src/api".</summary>
    public string DirectoryLabel
    {
        get
        {
            var relative = Path.GetRelativePath(_projectRoot, WorkingDirectory).Replace('\\', '/');
            return relative is "." or "" ? _projectName : relative.StartsWith("..", StringComparison.Ordinal) ? WorkingDirectory : $"{_projectName}/{relative}";
        }
    }

    public string ToolTip => $"{Profile.Name} · {DirectoryLabel}\n{Profile.CommandLine}";

    /// <summary>Bumped on every (re)start; reports carrying an older generation are ignored.</summary>
    public int Generation { get; private set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarting), nameof(IsRunning), nameof(IsEnded), nameof(HasFailed), nameof(EndedMessage), nameof(Tone), nameof(StateText))]
    public partial TerminalSessionState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndedMessage), nameof(Tone))]
    public partial int? ExitCode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndedMessage))]
    public partial string? ErrorMessage { get; private set; }

    public bool IsStarting => State == TerminalSessionState.Starting;

    public bool IsRunning => State == TerminalSessionState.Running;

    /// <summary>The process is gone (exited or never started): the restart bar is shown.</summary>
    public bool IsEnded => State is TerminalSessionState.Exited or TerminalSessionState.Failed;

    public bool HasFailed => State == TerminalSessionState.Failed;

    public bool IsClosed => _closed;

    /// <summary>"Process exited with code 1" / "PowerShell could not start: …".</summary>
    public string? EndedMessage => State switch
    {
        TerminalSessionState.Exited => ExitCode is { } code ? $"Process exited with code {code}" : "Process exited",
        TerminalSessionState.Failed => $"{Profile.Name} could not start{(string.IsNullOrWhiteSpace(ErrorMessage) ? "." : $": {ErrorMessage}")}",
        _ => null,
    };

    public string StateText => State switch
    {
        TerminalSessionState.Starting => "Starting",
        TerminalSessionState.Running => "Running",
        TerminalSessionState.Exited => "Exited",
        _ => "Failed to start",
    };

    /// <summary>Status dot of the session tab.</summary>
    public StatusTone Tone => State switch
    {
        TerminalSessionState.Running => StatusTone.Success,
        TerminalSessionState.Starting => StatusTone.Running,
        TerminalSessionState.Exited when ExitCode is 0 => StatusTone.Neutral,
        _ => StatusTone.Danger,
    };

    /// <summary>What "Clear" sends after clearing the screen so the shell redraws its prompt.</summary>
    public string ClearInput => Profile.Kind == ShellKind.CommandPrompt ? "cls\r" : "\f";

    public TerminalLaunch CurrentLaunch => new(Generation, Profile, WorkingDirectory);

    /// <summary>The app attached a terminal control: the shell starts now (once).</summary>
    public bool IsAttached => _backend is not null;

    /// <summary>Raised once when the session is closed (the app disposes its terminal control).</summary>
    public event EventHandler? Closed;

    /// <summary>Connects the app-side terminal. Starts the shell the first time.</summary>
    public void Attach(ITerminalBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (_closed || ReferenceEquals(_backend, backend))
        {
            return;
        }

        _backend = backend;
        if (!_launched && State == TerminalSessionState.Starting)
        {
            Launch();
        }
    }

    /// <summary>
    /// Moves the session to another folder: a shell that has not started yet simply starts there,
    /// one already started is restarted there.
    /// </summary>
    public void Relocate(string workingDirectory)
    {
        if (_closed || string.Equals(WorkingDirectory, workingDirectory, StringComparison.Ordinal))
        {
            return;
        }

        WorkingDirectory = workingDirectory;
        if (_launched)
        {
            Restart();
        }
    }

    /// <summary>Kills the shell (if running) and starts it again, optionally with another shell.</summary>
    public void Restart(ShellProfile? profile = null, string? title = null)
    {
        if (_closed)
        {
            return;
        }

        if (profile is not null)
        {
            Profile = profile;
        }

        if (title is not null)
        {
            Title = title;
        }

        StopBackend();
        Generation++;
        ExitCode = null;
        ErrorMessage = null;
        State = TerminalSessionState.Starting;
        _launched = false;
        if (_backend is not null)
        {
            Launch();
        }
    }

    public void Clear()
    {
        if (!_closed)
        {
            Try(() => _backend?.Clear(IsRunning ? ClearInput : null));
        }
    }

    public void Focus()
    {
        if (!_closed)
        {
            Try(() => _backend?.Focus());
        }
    }

    /// <summary>The shell process of <paramref name="generation"/> is running (any thread).</summary>
    public void ReportStarted(int generation) => OnUi(generation, () =>
    {
        if (State == TerminalSessionState.Starting)
        {
            State = TerminalSessionState.Running;
        }
    });

    /// <summary>The shell process of <paramref name="generation"/> ended (any thread).</summary>
    public void ReportExited(int generation, int? exitCode) => OnUi(generation, () =>
    {
        if (State is TerminalSessionState.Starting or TerminalSessionState.Running)
        {
            ExitCode = exitCode;
            State = TerminalSessionState.Exited;
        }
    });

    /// <summary>The shell of <paramref name="generation"/> could not be started (any thread).</summary>
    public void ReportFailed(int generation, string message) => OnUi(generation, () =>
    {
        ErrorMessage = message;
        ExitCode = null;
        State = TerminalSessionState.Failed;
    });

    /// <summary>Kills the shell and releases the terminal (raises <see cref="Closed"/>).</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopBackend();
        OnPropertyChanged(nameof(IsClosed));
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Launch()
    {
        _launched = true;

        // The folder may have been deleted or renamed since: start in the project instead of failing.
        if (!Directory.Exists(WorkingDirectory) && Directory.Exists(_projectRoot))
        {
            WorkingDirectory = _projectRoot;
        }

        var launch = CurrentLaunch;
        try
        {
            _backend!.Start(launch);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = TerminalSessionState.Failed;
        }
    }

    private void StopBackend()
    {
        if (_launched)
        {
            Try(() => _backend?.Stop());
        }
    }

    private void OnUi(int generation, Action apply) => _dispatcher.Post(() =>
    {
        if (!_closed && generation == Generation)
        {
            apply();
        }
    });

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Terminal backend call failed: {ex.Message}");
        }
    }

    public override string ToString() => $"{Title} ({State})";
}
