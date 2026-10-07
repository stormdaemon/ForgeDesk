using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Terminal;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Terminal;

/// <summary>Records what a session asks of the app-side terminal.</summary>
public sealed class FakeTerminalBackend : ITerminalBackend
{
    public List<TerminalLaunch> Starts { get; } = [];

    public int Stops { get; private set; }

    public List<string?> Clears { get; } = [];

    public int Focuses { get; private set; }

    public Exception? StartFailure { get; set; }

    public TerminalLaunch? Last => Starts.LastOrDefault();

    public void Start(TerminalLaunch launch)
    {
        if (StartFailure is { } failure)
        {
            throw failure;
        }

        Starts.Add(launch);
    }

    public void Stop() => Stops++;

    public void Clear(string? shellInput) => Clears.Add(shellInput);

    public void Focus() => Focuses++;
}

public sealed class TerminalSectionViewModelTests : IDisposable
{
    private static readonly ShellProfile Pwsh = new("pwsh", "PowerShell", ShellKind.PowerShellCore, @"C:\Program Files\PowerShell\7\pwsh.exe", "-NoLogo");
    private static readonly ShellProfile Cmd = new("cmd", "Command Prompt", ShellKind.CommandPrompt, @"C:\Windows\System32\cmd.exe", string.Empty);
    private static readonly ShellProfile Bash = new("git-bash", "Git Bash", ShellKind.GitBash, @"C:\Program Files\Git\bin\bash.exe", "--login -i");

    private readonly TestFolder _folder = new();
    private readonly IShellDiscovery _discovery = Substitute.For<IShellDiscovery>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IShellIntegration _shell = Substitute.For<IShellIntegration>();
    private readonly ProjectContext _context;
    private readonly WorkspaceServices _services;
    private readonly List<TerminalSectionViewModel> _sections = [];

    public TerminalSectionViewModelTests()
    {
        Directory.CreateDirectory(_folder.Combine("src", "api"));
        File.WriteAllText(_folder.Combine("src", "api", "app.cs"), "// app");
        _discovery.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(_ => Shells);
        _settings.Current.Returns(_ => Settings);
        var git = Substitute.For<IGitService>();
        var registry = Substitute.For<IProjectRegistry>();
        _services = new WorkspaceServices(Substitute.For<IWorkspaceSectionFactory>(), git, registry, Substitute.For<IProjectStatusService>(),
            Substitute.For<IRunService>(), Substitute.For<IWorkItemService>(), Substitute.For<IActivityLog>(), _settings, Substitute.For<IDialogService>(),
            _notifications, _shell, Substitute.For<INavigationService>(), Substitute.For<IProjectActions>(), ImmediateDispatcher.Instance);
        _context = new ProjectContext(TestData.Project("forge-app", _folder.Path), git, Substitute.For<IProjectDetector>(), registry, ImmediateDispatcher.Instance);
    }

    private IReadOnlyList<ShellProfile> Shells { get; set; } = [Pwsh, Cmd, Bash];

    private AppSettings Settings { get; set; } = AppSettings.Default;

    public void Dispose()
    {
        foreach (var section in _sections)
        {
            section.Dispose();
        }

        _context.Dispose();
        _folder.Dispose();
    }

    private TerminalSectionViewModel Create()
    {
        var section = new TerminalSectionViewModel(_context, _services, _discovery);
        _sections.Add(section);
        return section;
    }

    private async Task<TerminalSectionViewModel> ActivateAsync()
    {
        var section = Create();
        await section.ActivateAsync();
        return section;
    }

    [Fact]
    public void Default_shell_is_the_configured_one_when_installed_otherwise_the_first()
    {
        TerminalShells.ResolveDefault([Pwsh, Cmd], "cmd").Should().Be(Cmd);
        TerminalShells.ResolveDefault([Pwsh, Cmd], "CMD").Should().Be(Cmd);
        TerminalShells.ResolveDefault([Pwsh, Cmd], "wsl:Ubuntu").Should().Be(Pwsh);
        TerminalShells.ResolveDefault([Pwsh, Cmd], null).Should().Be(Pwsh);
        TerminalShells.ResolveDefault([], "cmd").Should().BeNull();
    }

    [Fact]
    public void Titles_are_numbered_per_shell()
    {
        TerminalShells.NextTitle("PowerShell", []).Should().Be("PowerShell");
        TerminalShells.NextTitle("PowerShell", ["PowerShell"]).Should().Be("PowerShell (2)");
        TerminalShells.NextTitle("PowerShell", ["PowerShell", "PowerShell (2)", "Git Bash"]).Should().Be("PowerShell (3)");
    }

    [Fact]
    public async Task First_activation_opens_the_default_shell_in_the_project_folder()
    {
        Settings = AppSettings.Default with { DefaultShellId = "git-bash", TerminalFontSize = 15 };

        var section = await ActivateAsync();

        section.Shells.Should().Equal(Pwsh, Cmd, Bash);
        section.DefaultShell.Should().Be(Bash);
        section.FontSize.Should().Be(15);
        section.Sessions.Should().ContainSingle();
        var session = section.SelectedSession!;
        session.Profile.Should().Be(Bash);
        session.Title.Should().Be("Git Bash");
        session.WorkingDirectory.Should().Be(_context.Root);
        session.DirectoryLabel.Should().Be("forge-app");
        session.State.Should().Be(TerminalSessionState.Starting);
        section.ShowEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Later_activations_do_not_open_more_terminals()
    {
        var section = await ActivateAsync();
        section.Deactivate();
        await section.ActivateAsync();

        section.CloseSessionCommand.Execute(section.SelectedSession);
        section.Deactivate();
        await section.ActivateAsync();

        section.Sessions.Should().BeEmpty();
        section.ShowEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task The_shell_starts_when_the_app_attaches_its_terminal()
    {
        var section = await ActivateAsync();
        var session = section.SelectedSession!;
        var backend = new FakeTerminalBackend();

        session.Attach(backend);

        backend.Starts.Should().ContainSingle();
        backend.Last!.Profile.Should().Be(Pwsh);
        backend.Last.WorkingDirectory.Should().Be(_context.Root);
        backend.Last.CommandLine.Should().Be("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo");
        backend.Last.Generation.Should().Be(1);

        session.Attach(backend);
        backend.Starts.Should().ContainSingle("attaching the same terminal again does not restart the shell");
    }

    [Fact]
    public async Task Session_lifecycle_running_exited_restart()
    {
        var section = await ActivateAsync();
        var session = section.SelectedSession!;
        var backend = new FakeTerminalBackend();
        session.Attach(backend);

        session.ReportStarted(1);
        session.State.Should().Be(TerminalSessionState.Running);
        session.Tone.Should().Be(StatusTone.Success);

        session.ReportExited(1, 3);
        session.State.Should().Be(TerminalSessionState.Exited);
        session.IsEnded.Should().BeTrue();
        session.ExitCode.Should().Be(3);
        session.EndedMessage.Should().Be("Process exited with code 3");
        session.Tone.Should().Be(StatusTone.Danger);

        section.RestartSessionCommand.Execute(session);

        backend.Stops.Should().Be(1);
        backend.Starts.Should().HaveCount(2);
        backend.Last!.Generation.Should().Be(2);
        session.State.Should().Be(TerminalSessionState.Starting);
        session.ExitCode.Should().BeNull();

        session.ReportExited(1, 0);
        session.State.Should().Be(TerminalSessionState.Starting, "a report about the replaced process is ignored");

        session.ReportStarted(2);
        session.ReportExited(2, 0);
        session.Tone.Should().Be(StatusTone.Neutral);
        session.EndedMessage.Should().Be("Process exited with code 0");
    }

    [Fact]
    public async Task A_shell_that_fails_to_start_can_be_replaced_by_another()
    {
        var section = await ActivateAsync();
        var session = section.SelectedSession!;
        var backend = new FakeTerminalBackend();
        session.Attach(backend);

        session.ReportFailed(1, "pwsh.exe was not found.");

        session.State.Should().Be(TerminalSessionState.Failed);
        session.HasFailed.Should().BeTrue();
        session.EndedMessage.Should().Be("PowerShell could not start: pwsh.exe was not found.");

        section.ReplaceShellCommand.Execute(Cmd);

        session.Profile.Should().Be(Cmd);
        session.Title.Should().Be("Command Prompt");
        backend.Last!.Profile.Should().Be(Cmd);
        backend.Last.Generation.Should().Be(2);
        session.State.Should().Be(TerminalSessionState.Starting);
    }

    [Fact]
    public async Task A_backend_that_throws_fails_the_session()
    {
        var section = await ActivateAsync();
        var session = section.SelectedSession!;

        session.Attach(new FakeTerminalBackend { StartFailure = new InvalidOperationException("ConPTY is not available") });

        session.State.Should().Be(TerminalSessionState.Failed);
        session.ErrorMessage.Should().Be("ConPTY is not available");
    }

    [Fact]
    public async Task New_sessions_use_the_chosen_shell_and_numbered_titles()
    {
        var section = await ActivateAsync();

        await section.NewDefaultSessionCommand.ExecuteAsync(null);
        await section.NewSessionCommand.ExecuteAsync(Cmd);

        section.Sessions.Select(s => s.Title).Should().Equal("PowerShell", "PowerShell (2)", "Command Prompt");
        section.SelectedSession!.Profile.Should().Be(Cmd);
        section.Sessions.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Closing_a_session_stops_it_and_selects_a_neighbor()
    {
        var section = await ActivateAsync();
        await section.NewDefaultSessionCommand.ExecuteAsync(null);
        await section.NewDefaultSessionCommand.ExecuteAsync(null);
        var middle = section.Sessions[1];
        var backend = new FakeTerminalBackend();
        middle.Attach(backend);
        var closed = false;
        middle.Closed += (_, _) => closed = true;
        section.SelectedSession = middle;

        section.CloseSessionCommand.Execute(null);

        closed.Should().BeTrue();
        middle.IsClosed.Should().BeTrue();
        backend.Stops.Should().Be(1);
        section.Sessions.Should().HaveCount(2).And.NotContain(middle);
        section.SelectedSession.Should().BeSameAs(section.Sessions[1]);

        middle.ReportStarted(1);
        middle.State.Should().Be(TerminalSessionState.Starting, "a closed session ignores late reports");
    }

    [Fact]
    public async Task Close_others_keeps_one_session()
    {
        var section = await ActivateAsync();
        await section.NewDefaultSessionCommand.ExecuteAsync(null);
        var keep = section.Sessions[0];

        section.CloseOtherSessionsCommand.Execute(keep);

        section.Sessions.Should().Equal(keep);
        section.SelectedSession.Should().BeSameAs(keep);
    }

    [Fact]
    public async Task Navigating_with_a_folder_opens_a_terminal_there()
    {
        var section = await ActivateAsync();
        section.SelectedSession!.Attach(new FakeTerminalBackend());
        section.Deactivate();
        await section.ActivateAsync();

        await section.NavigateToAsync("src/api");

        section.Sessions.Should().HaveCount(2);
        section.SelectedSession!.WorkingDirectory.Should().Be(_folder.Combine("src", "api"));
        section.SelectedSession.DirectoryLabel.Should().Be("forge-app/src/api");
    }

    [Fact]
    public async Task Navigating_right_after_the_first_activation_reuses_the_pending_terminal()
    {
        var section = await ActivateAsync();

        await section.NavigateToAsync("src");

        section.Sessions.Should().ContainSingle();
        section.SelectedSession!.WorkingDirectory.Should().Be(_folder.Combine("src"));
    }

    [Fact]
    public async Task Navigating_right_after_the_first_activation_restarts_a_started_terminal_there()
    {
        var section = await ActivateAsync();
        var backend = new FakeTerminalBackend();
        section.SelectedSession!.Attach(backend);

        await section.NavigateToAsync("src");

        section.Sessions.Should().ContainSingle();
        backend.Stops.Should().Be(1);
        backend.Last!.WorkingDirectory.Should().Be(_folder.Combine("src"));
        backend.Last.Generation.Should().Be(2);
    }

    [Fact]
    public async Task A_file_path_opens_its_folder_and_a_missing_folder_falls_back_to_the_root()
    {
        var section = await ActivateAsync();
        section.SelectedSession!.Attach(new FakeTerminalBackend());
        section.Deactivate();
        await section.ActivateAsync();

        await section.NavigateToAsync("src/api/app.cs");
        section.SelectedSession!.WorkingDirectory.Should().Be(_folder.Combine("src", "api"));

        await section.NavigateToAsync("does/not/exist");
        section.SelectedSession!.WorkingDirectory.Should().Be(_context.Root);
        _notifications.Received().Show("Folder not found", Arg.Any<string?>(), NotificationSeverity.Warning, Arg.Any<NotificationAction?>());
    }

    [Fact]
    public async Task No_shell_found_is_an_error_with_retry()
    {
        Shells = [];

        var section = await ActivateAsync();

        section.Error.Should().NotBeNull();
        section.Error!.Hint.Should().Contain("PowerShell");
        section.Sessions.Should().BeEmpty();
        section.ShowEmpty.Should().BeFalse();
        section.NewDefaultSessionCommand.CanExecute(null).Should().BeFalse();

        Shells = [Cmd];
        await section.RefreshCommand.ExecuteAsync(null);

        section.Error.Should().BeNull();
        section.DefaultShell.Should().Be(Cmd);
        section.ShowEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Clear_sends_the_shell_its_clear_key()
    {
        var section = await ActivateAsync();
        var backend = new FakeTerminalBackend();
        section.SelectedSession!.Attach(backend);
        section.SelectedSession.ReportStarted(1);

        section.ClearCommand.Execute(null);
        section.ReplaceShellCommand.Execute(Cmd);
        section.SelectedSession.ReportStarted(2);
        section.ClearCommand.Execute(null);

        backend.Clears.Should().Equal("\f", "cls\r");
    }

    [Fact]
    public async Task External_terminal_opens_in_the_session_folder()
    {
        var section = await ActivateAsync();
        await section.NavigateToAsync("src");

        section.OpenExternalTerminalCommand.Execute(null);

        _shell.Received().OpenExternalTerminal(_folder.Combine("src"));
    }

    [Fact]
    public async Task Settings_changes_update_font_size_and_default_shell()
    {
        var section = await ActivateAsync();

        _settings.Changed += Raise.Event<EventHandler<AppSettings>>(_settings, AppSettings.Default with { TerminalFontSize = 18, DefaultShellId = "cmd" });

        section.FontSize.Should().Be(18);
        section.DefaultShell.Should().Be(Cmd);
    }

    [Fact]
    public async Task Next_and_previous_cycle_through_sessions()
    {
        var section = await ActivateAsync();
        await section.NewDefaultSessionCommand.ExecuteAsync(null);
        section.SelectedSession = section.Sessions[1];

        section.SelectNextSessionCommand.Execute(null);
        section.SelectedSession.Should().BeSameAs(section.Sessions[0]);
        section.SelectPreviousSessionCommand.Execute(null);
        section.SelectedSession.Should().BeSameAs(section.Sessions[1]);
    }

    [Fact]
    public async Task Selecting_a_session_while_active_focuses_its_terminal()
    {
        var section = await ActivateAsync();
        await section.NewDefaultSessionCommand.ExecuteAsync(null);
        var first = section.Sessions[0];
        var backend = new FakeTerminalBackend();
        first.Attach(backend);

        section.SelectedSession = first;

        backend.Focuses.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Closing_the_project_stops_every_terminal()
    {
        var section = await ActivateAsync();
        await section.NewDefaultSessionCommand.ExecuteAsync(null);
        var backends = section.Sessions.Select(s =>
        {
            var backend = new FakeTerminalBackend();
            s.Attach(backend);
            return backend;
        }).ToList();
        var sessions = section.Sessions.ToList();

        _context.Dispose();

        backends.Should().OnlyContain(b => b.Stops == 1);
        sessions.Should().OnlyContain(s => s.IsClosed);
        section.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Ctrl_backtick_opens_a_terminal_only_when_none_is_open()
    {
        var section = await ActivateAsync();
        var backend = new FakeTerminalBackend();
        section.SelectedSession!.Attach(backend);

        await section.EnsureSessionCommand.ExecuteAsync(null);
        section.Sessions.Should().ContainSingle();
        backend.Focuses.Should().BeGreaterThan(0);

        section.CloseSessionCommand.Execute(null);
        await section.EnsureSessionCommand.ExecuteAsync(null);
        section.Sessions.Should().ContainSingle();
        section.SelectedSession!.Profile.Should().Be(Pwsh);
    }

    [Fact]
    public async Task A_deleted_folder_restarts_in_the_project_root()
    {
        var section = await ActivateAsync();
        section.SelectedSession!.Attach(new FakeTerminalBackend());
        section.Deactivate();
        await section.ActivateAsync();
        await section.NavigateToAsync("src/api");
        var session = section.SelectedSession!;
        var backend = new FakeTerminalBackend();

        Directory.Delete(_folder.Combine("src", "api"), recursive: true);
        session.Attach(backend);

        backend.Last!.WorkingDirectory.Should().Be(_context.Root);
        session.WorkingDirectory.Should().Be(_context.Root);
    }

    [Fact]
    public async Task Switching_tabs_keeps_sessions_running()
    {
        var section = await ActivateAsync();
        var backend = new FakeTerminalBackend();
        section.SelectedSession!.Attach(backend);

        section.Deactivate();
        await section.ActivateAsync();

        backend.Stops.Should().Be(0);
        section.Sessions.Should().ContainSingle();
    }
}
