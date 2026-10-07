using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Terminal;

/// <summary>Shell selection rules of the integrated terminal.</summary>
public static class TerminalShells
{
    /// <summary>The user's default shell (Settings → Terminal) when installed, otherwise the best discovered one.</summary>
    public static ShellProfile? ResolveDefault(IReadOnlyList<ShellProfile> shells, string? defaultShellId)
    {
        ArgumentNullException.ThrowIfNull(shells);
        if (!string.IsNullOrWhiteSpace(defaultShellId)
            && shells.FirstOrDefault(s => string.Equals(s.Id, defaultShellId, StringComparison.OrdinalIgnoreCase)) is { } preferred)
        {
            return preferred;
        }

        return shells.Count > 0 ? shells[0] : null;
    }

    /// <summary>"PowerShell", then "PowerShell (2)", "PowerShell (3)"… for sessions of the same shell.</summary>
    public static string NextTitle(string shellName, IEnumerable<string> existingTitles)
    {
        ArgumentNullException.ThrowIfNull(existingTitles);
        var taken = new HashSet<string>(existingTitles, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(shellName))
        {
            return shellName;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{shellName} ({n})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}

/// <summary>
/// The Terminal tab: integrated terminals (one per session) running the installed shells in the
/// project folder. Sessions survive tab and project switches and are closed with the project.
/// The first visit opens the default shell; "+" / Ctrl+Shift+T open more; navigating here with a
/// folder (from the Files tab) opens a terminal in that folder.
/// </summary>
public sealed partial class TerminalSectionViewModel : ViewModelBase, IWorkspaceSectionViewModel, INavigationTarget, IRefreshable, IDisposable
{
    private readonly ProjectContext _context;
    private readonly WorkspaceServices _services;
    private readonly IShellDiscovery _discovery;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private Task? _shellsLoad;
    private TerminalSessionViewModel? _autoSession;
    private int _nextId = 1;
    private bool _autoStarted;
    private bool _isActive;
    private bool _disposed;

    public TerminalSectionViewModel(ProjectContext context, WorkspaceServices services, IShellDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        _context = context;
        _services = services;
        _discovery = discovery;
        FontSize = ClampFontSize(services.Settings.Current.TerminalFontSize);
        services.Settings.Changed += OnSettingsChanged;
        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(ShowEmpty));
        };

        // Closing the project (or ForgeDesk) kills every shell of this workspace.
        _lifetimeRegistration = context.Lifetime.Register(() => services.Dispatcher.Post(Dispose));
    }

    public WorkspaceSection Section => WorkspaceSection.Terminal;

    public string ProjectName => _context.Project.Name;

    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = [];

    /// <summary>Installed shells, best first.</summary>
    public ObservableCollection<ShellProfile> Shells { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseSessionCommand), nameof(RestartSessionCommand), nameof(ClearCommand))]
    public partial TerminalSessionViewModel? SelectedSession { get; set; }

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>No session is open: the empty state lists the shells to start.</summary>
    public bool ShowEmpty => Sessions.Count == 0 && !IsDiscovering && Error is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsDiscovering { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShells), nameof(DefaultShellName))]
    [NotifyCanExecuteChangedFor(nameof(NewDefaultSessionCommand))]
    public partial ShellProfile? DefaultShell { get; private set; }

    public bool HasShells => DefaultShell is not null;

    public string DefaultShellName => DefaultShell?.Name ?? "terminal";

    /// <summary>Terminal font size (Settings → Terminal).</summary>
    [ObservableProperty]
    public partial double FontSize { get; private set; }

    // ----- Lifecycle --------------------------------------------------------------------

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        _isActive = true;
        await EnsureShellsAsync().ConfigureAwait(true);
        if (!_autoStarted && Sessions.Count == 0 && DefaultShell is { } shell && !_disposed)
        {
            _autoStarted = true;
            _autoSession = CreateSession(shell, null);
        }

        SelectedSession?.Focus();
    }

    public void Deactivate()
    {
        _isActive = false;

        // Once the user has seen the automatic terminal, it is theirs: never move it.
        _autoSession = null;
    }

    /// <summary>A folder path (relative to the project) opens a new terminal there.</summary>
    public async Task NavigateToAsync(object argument)
    {
        if (_disposed || argument is not string text)
        {
            return;
        }

        await EnsureShellsAsync().ConfigureAwait(true);
        var directory = ResolveDirectory(FileLocation.Normalize(text));

        // The activation that brought us here just opened the default terminal: move it to the
        // folder instead of leaving a second, unwanted terminal behind.
        if (_autoSession is { IsClosed: false } pending && Sessions.Count == 1 && ReferenceEquals(Sessions[0], pending))
        {
            _autoSession = null;
            pending.Relocate(directory);
            SelectedSession = pending;
            return;
        }

        _autoSession = null;
        if (DefaultShell is { } shell)
        {
            CreateSession(shell, directory);
        }
        else
        {
            _services.Notifications.Show("No shell found", "ForgeDesk could not find PowerShell, Command Prompt or another shell on this PC.", NotificationSeverity.Warning);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Settings.Changed -= OnSettingsChanged;
        _lifetimeRegistration.Dispose();
        foreach (var session in Sessions.ToList())
        {
            session.Dispose();
        }

        Sessions.Clear();
        SelectedSession = null;
    }

    // ----- Shells -----------------------------------------------------------------------

    /// <summary>The shell discovery in flight (tests wait on it).</summary>
    internal Task EnsureShellsAsync() => _shellsLoad ??= DiscoverAsync();

    /// <summary>F5: discovers the installed shells again (a shell installed meanwhile appears).</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        _shellsLoad = DiscoverAsync();
        return _shellsLoad;
    }

    private async Task DiscoverAsync()
    {
        IsDiscovering = true;
        try
        {
            await RunAsync(async () =>
            {
                var shells = await _discovery.DiscoverAsync(_context.Lifetime).ConfigureAwait(true);
                Shells.Clear();
                foreach (var shell in shells)
                {
                    Shells.Add(shell);
                }

                DefaultShell = TerminalShells.ResolveDefault(shells, _services.Settings.Current.DefaultShellId);
                if (DefaultShell is null)
                {
                    throw new ForgeException(ErrorKind.ToolNotFound, "No shell was found on this PC.",
                        "Install PowerShell 7 (winget install Microsoft.PowerShell) or check that Command Prompt is available, then press Retry.");
                }
            }, errorTitle: "No terminal available").ConfigureAwait(true);
        }
        finally
        {
            IsDiscovering = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => _services.Dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        FontSize = ClampFontSize(settings.TerminalFontSize);
        if (Shells.Count > 0)
        {
            DefaultShell = TerminalShells.ResolveDefault(Shells, settings.DefaultShellId);
        }
    });

    private static double ClampFontSize(double size) => double.IsFinite(size) ? Math.Clamp(size, 8, 32) : 13;

    // ----- Sessions ---------------------------------------------------------------------

    /// <summary>Opens a terminal with <paramref name="profile"/> (the default shell when null) in the project folder.</summary>
    [RelayCommand]
    private async Task NewSessionAsync(ShellProfile? profile)
    {
        await EnsureShellsAsync().ConfigureAwait(true);
        _autoSession = null;
        if ((profile ?? DefaultShell) is { } shell)
        {
            CreateSession(shell, null);
        }
    }

    /// <summary>Ctrl+Shift+T and the "+" button: a new terminal with the default shell.</summary>
    [RelayCommand(CanExecute = nameof(HasShells))]
    private Task NewDefaultSessionAsync() => NewSessionAsync(null);

    /// <summary>
    /// Ctrl+` while the tab is shown: opens the default shell when no terminal is open, otherwise
    /// just focuses the current one (new terminals come from "+" or Ctrl+Shift+T).
    /// </summary>
    [RelayCommand]
    private async Task EnsureSessionAsync()
    {
        if (Sessions.Count > 0)
        {
            SelectedSession ??= Sessions[0];
            SelectedSession.Focus();
            return;
        }

        await NewSessionAsync(null).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private void CloseSession(TerminalSessionViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null)
        {
            return;
        }

        var index = Sessions.IndexOf(session);
        if (index < 0)
        {
            return;
        }

        if (ReferenceEquals(_autoSession, session))
        {
            _autoSession = null;
        }

        var wasSelected = ReferenceEquals(SelectedSession, session);
        Sessions.RemoveAt(index);
        session.Dispose();
        if (wasSelected)
        {
            SelectedSession = Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)];
            SelectedSession?.Focus();
        }
    }

    /// <summary>Closes every session except <paramref name="session"/>.</summary>
    [RelayCommand]
    private void CloseOtherSessions(TerminalSessionViewModel? session)
    {
        session ??= SelectedSession;
        foreach (var other in Sessions.Where(s => !ReferenceEquals(s, session)).ToList())
        {
            CloseSession(other);
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private void RestartSession(TerminalSessionViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null)
        {
            return;
        }

        _autoSession = null;
        session.Restart();
        session.Focus();
    }

    /// <summary>"Choose another shell" (failed start) or "Switch shell": restarts the selected session with <paramref name="profile"/>.</summary>
    [RelayCommand]
    private void ReplaceShell(ShellProfile? profile)
    {
        if (profile is null || SelectedSession is not { } session)
        {
            return;
        }

        var title = TerminalShells.NextTitle(profile.Name, Sessions.Where(s => !ReferenceEquals(s, session)).Select(s => s.Title));
        _autoSession = null;
        session.Restart(profile, title);
        session.Focus();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSession))]
    private void Clear()
    {
        SelectedSession?.Clear();
        SelectedSession?.Focus();
    }

    [RelayCommand]
    private void OpenExternalTerminal(TerminalSessionViewModel? session)
    {
        var directory = (session ?? SelectedSession)?.WorkingDirectory ?? _context.Root;
        try
        {
            _services.Shell.OpenExternalTerminal(directory);
        }
        catch (Exception ex)
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open a terminal"));
        }
    }

    [RelayCommand]
    private void CopyWorkingDirectory(TerminalSessionViewModel? session)
    {
        if ((session ?? SelectedSession)?.WorkingDirectory is not { } directory)
        {
            return;
        }

        try
        {
            _services.Shell.CopyToClipboard(directory);
            _services.Notifications.Show("Path copied", directory);
        }
        catch (Exception ex)
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy the path"));
        }
    }

    [RelayCommand]
    private void SelectNextSession() => MoveSelection(+1);

    [RelayCommand]
    private void SelectPreviousSession() => MoveSelection(-1);

    private bool CanActOnSession(TerminalSessionViewModel? session) => (session ?? SelectedSession) is not null;

    private bool HasSelectedSession() => SelectedSession is not null;

    private void MoveSelection(int delta)
    {
        if (Sessions.Count < 2)
        {
            return;
        }

        var index = SelectedSession is null ? 0 : Sessions.IndexOf(SelectedSession);
        SelectedSession = Sessions[((index + delta) % Sessions.Count + Sessions.Count) % Sessions.Count];
    }

    private TerminalSessionViewModel? CreateSession(ShellProfile profile, string? workingDirectory)
    {
        if (_disposed)
        {
            return null;
        }

        var directory = workingDirectory ?? ResolveDirectory(null);
        var title = TerminalShells.NextTitle(profile.Name, Sessions.Select(s => s.Title));
        var session = new TerminalSessionViewModel(_nextId++, title, profile, directory, _context.Root, _context.Project.Name, _services.Dispatcher);
        Sessions.Add(session);
        SelectedSession = session;
        return session;
    }

    /// <summary>
    /// The folder a terminal starts in: the project folder or a sub-folder of it. A file opens its
    /// folder; a folder that no longer exists falls back to the project root.
    /// </summary>
    internal string ResolveDirectory(string? relativePath)
    {
        var root = _context.Root;
        if (string.IsNullOrEmpty(relativePath))
        {
            return root;
        }

        try
        {
            var full = PathUtil.ResolveUnder(root, relativePath);
            if (File.Exists(full))
            {
                full = Path.GetDirectoryName(full) ?? root;
            }

            if (Directory.Exists(full))
            {
                return full;
            }

            _services.Notifications.Show("Folder not found", $"'{relativePath}' does not exist. The terminal opens in the project folder.", NotificationSeverity.Warning);
        }
        catch (Exception ex) when (ex is ForgeException or ArgumentException or IOException)
        {
            _services.Notifications.Show("Folder not available", $"The terminal opens in the project folder instead of '{relativePath}'.", NotificationSeverity.Warning);
        }

        return root;
    }

    partial void OnSelectedSessionChanged(TerminalSessionViewModel? value)
    {
        if (_isActive)
        {
            value?.Focus();
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e?.PropertyName == nameof(Error))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowEmpty)));
        }
    }
}
