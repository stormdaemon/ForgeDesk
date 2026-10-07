using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using EasyWindowsTerminalControl.Internals;
using ForgeDesk.App.Theming;
using ForgeDesk.Presentation.Terminal;
using Microsoft.Terminal.Wpf;

namespace ForgeDesk.App.Features.Terminal;

/// <summary>
/// The live terminal of one session: a Windows Terminal control (an HWND hosted in WPF) connected
/// to a ConPTY pseudo console (<see cref="TermPTY"/>) running the shell. The same control is kept
/// for the whole life of the session and re-parented when the Terminal tab's view is rebuilt
/// (switching projects), so the screen, scrollback and process survive. Starting waits until the
/// control has a window (output written before that would be lost); the process exit (and its
/// code) is watched independently of the pseudo console, which is then closed so the PTY thread ends.
/// </summary>
internal sealed class TerminalSessionHost : ITerminalBackend, IDisposable
{
    private static readonly TimeSpan OutputDrainDelay = TimeSpan.FromMilliseconds(200);

    private readonly TerminalSessionViewModel _session;
    private readonly Dispatcher _dispatcher;
    private readonly Lock _gate = new();
    private TermPTY? _term;
    private Process? _process;
    private TerminalLaunch? _pending;
    private double _fontSize = 13;
    private bool _disposed;

    public TerminalSessionHost(TerminalSessionViewModel session, Dispatcher dispatcher)
    {
        _session = session;
        _dispatcher = dispatcher;
        Control = new TerminalControl
        {
            AutoResize = true,
            Focusable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(Control, $"Terminal.Control.{session.Id}");
        AutomationProperties.SetName(Control, $"Terminal {session.Title}");
        Control.Loaded += OnControlLoaded;
        ThemeProbe.Changed += OnThemeChanged;

        // Starts the shell (deferred until the control is in a window).
        session.Attach(this);
    }

    public TerminalSessionViewModel Session => _session;

    /// <summary>The terminal control; the view parents it (one per session, never recreated).</summary>
    public TerminalControl Control { get; }

    private bool HasWindow => PresentationSource.FromVisual(Control) is not null;

    // ----- ITerminalBackend ---------------------------------------------------------

    public void Start(TerminalLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        OnUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            StopProcess();
            if (!HasWindow)
            {
                _pending = launch;
                return;
            }

            StartCore(launch);
        });
    }

    public void Stop() => OnUi(() =>
    {
        _pending = null;
        StopProcess();
    });

    public void Clear(string? shellInput) => OnUi(() =>
    {
        var term = _term;
        if (term is null)
        {
            return;
        }

        try
        {
            term.ClearUITerminal();
            if (!string.IsNullOrEmpty(shellInput))
            {
                term.WriteToTerm(shellInput);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.IO.IOException)
        {
            Trace.TraceWarning($"Could not clear the terminal: {ex.Message}");
        }
    });

    public void Focus() => _dispatcher.BeginInvoke(() =>
    {
        if (!_disposed && Control.IsVisible)
        {
            Control.Focus();
        }
    }, DispatcherPriority.Input);

    // ----- View helpers -------------------------------------------------------------

    /// <summary>Copies the selection (Ctrl+Shift+C). Returns false when nothing is selected.</summary>
    public bool CopySelection()
    {
        var text = Control.GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Pastes the clipboard text into the shell (Ctrl+Shift+V).</summary>
    public void Paste()
    {
        if (_term is not { } term || !_session.IsRunning)
        {
            return;
        }

        string text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }

        if (text.Length > 0)
        {
            try
            {
                // Terminals send CR for Enter; CRLF would run each line twice in some shells.
                term.WriteToTerm(text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r'));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.IO.IOException)
            {
                Trace.TraceWarning($"Could not paste into the terminal: {ex.Message}");
            }
        }
    }

    public void SetFontSize(double fontSize)
    {
        if (Math.Abs(fontSize - _fontSize) < 0.01)
        {
            return;
        }

        _fontSize = fontSize;
        ApplyTheme();
    }

    /// <summary>Removes the control from the panel that currently hosts it (before re-parenting).</summary>
    public void DetachFromParent()
    {
        switch (Control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(Control);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, Control):
                decorator.Child = null;
                break;
            case ContentControl content when ReferenceEquals(content.Content, Control):
                content.Content = null;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending = null;
        ThemeProbe.Changed -= OnThemeChanged;
        Control.Loaded -= OnControlLoaded;
        StopProcess();
        try
        {
            Control.Connection = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        DetachFromParent();

        // Destroys the native terminal (the HwndHost would otherwise stay parked until exit).
        foreach (var host in FindHwndHosts(Control))
        {
            host.Dispose();
        }
    }

    // ----- Process ------------------------------------------------------------------

    private void StartCore(TerminalLaunch launch)
    {
        _pending = null;
        ApplyTheme();
        var term = new TermPTY();
        var factory = new TrackingProcessFactory();
        lock (_gate)
        {
            _term = term;
        }

        var generation = launch.Generation;
        var columns = Control.Columns > 0 ? Control.Columns : 120;
        var rows = Control.Rows > 0 ? Control.Rows : 30;
        term.TermReady += (_, _) => OnTermReady(term, factory, generation);

        var commandLine = launch.CommandLine;
        var directory = launch.WorkingDirectory;
        _ = Task.Run(() =>
        {
            try
            {
                // Blocks this worker thread for the whole life of the shell (it pumps the output).
                term.Start(commandLine, columns, rows, false, factory, directory);
            }
            catch (Exception ex)
            {
                if (!factory.HasStarted)
                {
                    _session.ReportFailed(generation, Describe(ex, launch));
                }
                else
                {
                    Trace.TraceWarning($"Terminal session ended with an error: {ex.Message}");
                }
            }
        });
    }

    /// <summary>On the PTY thread, before it starts pumping output: connect the control synchronously so nothing is lost.</summary>
    private void OnTermReady(TermPTY term, TrackingProcessFactory factory, int generation)
    {
        var current = false;
        try
        {
            _dispatcher.Invoke(() =>
            {
                lock (_gate)
                {
                    current = !_disposed && ReferenceEquals(_term, term);
                }

                if (!current)
                {
                    return;
                }

                Control.Connection = term;
                term.Win32DirectInputMode(true);
                if (Control.Columns > 0 && Control.Rows > 0)
                {
                    term.Resize(Control.Columns, Control.Rows);
                }

                _process = factory.Process;
            });
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            current = false;
        }

        if (!current)
        {
            // Replaced or closed while starting: do not leave the shell behind.
            Kill(factory.Process);
            CloseConsole(term);
            return;
        }

        _session.ReportStarted(generation);
        _ = WatchExitAsync(term, factory.Process, generation);
    }

    private async Task WatchExitAsync(TermPTY term, Process? process, int generation)
    {
        int? exitCode = null;
        if (process is not null)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                exitCode = null;
            }
        }

        // Let the last output reach the screen, then end the PTY thread.
        await Task.Delay(OutputDrainDelay).ConfigureAwait(false);
        CloseConsole(term);
        _session.ReportExited(generation, exitCode);
    }

    private void StopProcess()
    {
        TermPTY? term;
        lock (_gate)
        {
            term = _term;
            _term = null;
        }

        var process = _process;
        _process = null;
        Kill(process);
        if (term is not null)
        {
            try
            {
                term.CloseStdinToApp();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.IO.IOException)
            {
            }

            CloseConsole(term);
        }
    }

    private static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            Trace.TraceWarning($"Could not stop the shell: {ex.Message}");
        }
    }

    private static void CloseConsole(TermPTY term)
    {
        try
        {
            ((ITerminalConnection)term).Close();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.IO.IOException)
        {
        }
    }

    private static string Describe(Exception exception, TerminalLaunch launch) => exception switch
    {
        Win32Exception { NativeErrorCode: 2 or 3 } => $"{launch.Profile.Executable} was not found. The shell may have been uninstalled or moved.",
        Win32Exception { NativeErrorCode: 267 } => $"the folder '{launch.WorkingDirectory}' does not exist.",
        Win32Exception { NativeErrorCode: 5 } => "Windows denied access to the program.",
        Win32Exception { NativeErrorCode: 193 or 216 } => $"{launch.Profile.Executable} is not a program this PC can run.",
        _ => exception.Message,
    };

    // ----- Theme and lifecycle ------------------------------------------------------

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        if (_pending is { } launch)
        {
            StartCore(launch);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        if (_disposed || !HasWindow)
        {
            return;
        }

        try
        {
            var background = TerminalThemes.Background();
            Control.SetTheme(TerminalThemes.Create(), TerminalThemes.FontFamily, (short)Math.Round(_fontSize), background);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or DllNotFoundException)
        {
            Trace.TraceWarning($"Could not theme the terminal: {ex.Message}");
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private static IEnumerable<HwndHost> FindHwndHosts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is HwndHost host)
            {
                yield return host;
            }

            foreach (var nested in FindHwndHosts(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Starts the shell like the library does, keeping a handle on the process for its exit code.</summary>
    private sealed class TrackingProcessFactory : IProcessFactory
    {
        public Process? Process { get; private set; }

        public bool HasStarted { get; private set; }

        public IProcess Start(string command, nuint attributes, PseudoConsole console, string? workingDirectory = null)
        {
            var wrapped = ProcessFactory.Start(command, attributes, console, workingDirectory);
            HasStarted = true;
            try
            {
                // Open our own handle now: the exit code stays readable after the process ends.
                var process = wrapped.Process;
                _ = process.Handle;
                Process = process;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone: the exit is reported without a code.
                Process = null;
            }

            return wrapped;
        }
    }
}
