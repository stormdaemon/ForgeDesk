using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Tests.Settings.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class TerminalCommandsEditorSettingsTests : IDisposable
{
    private static readonly ShellProfile[] Profiles =
    [
        new("pwsh", "PowerShell 7", ShellKind.PowerShellCore, @"C:\Program Files\PowerShell\7\pwsh.exe", "-NoLogo"),
        new("cmd", "Command Prompt", ShellKind.CommandPrompt, @"C:\Windows\System32\cmd.exe", ""),
        new("gitbash", "Git Bash", ShellKind.GitBash, @"C:\Program Files\Git\bin\bash.exe", "--login -i"),
    ];

    private readonly SettingsHarness _harness = new();

    public TerminalCommandsEditorSettingsTests()
    {
        _harness.Shells.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(Profiles);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Shells_are_listed_with_automatic_first_and_icons_per_kind()
    {
        var terminal = new TerminalSettingsViewModel(_harness.CreateStore(), _harness.Shells);

        await terminal.ActivateAsync();

        terminal.Shells.Select(s => s.Name).Should().Equal("Automatic", "PowerShell 7", "Command Prompt", "Git Bash");
        terminal.Shells[0].Detail.Should().Be("Currently PowerShell 7");
        terminal.Shells[1].Icon.Should().Be("WindowConsole20");
        terminal.Shells[3].Icon.Should().Be("BranchFork20");
        terminal.SelectedShell!.Id.Should().BeNull("no shell was chosen yet");
        _harness.Settings.SaveCount.Should().Be(0, "loading must not save");
    }

    [Fact]
    public async Task Choosing_a_shell_saves_it()
    {
        var terminal = new TerminalSettingsViewModel(_harness.CreateStore(), _harness.Shells);
        await terminal.ActivateAsync();

        terminal.SelectedShell = terminal.Shells.Single(s => s.Id == "gitbash");

        _harness.Settings.Current.DefaultShellId.Should().Be("gitbash");

        terminal.SelectedShell = terminal.Shells[0];

        _harness.Settings.Current.DefaultShellId.Should().BeNull();
    }

    [Fact]
    public async Task An_uninstalled_shell_falls_back_to_automatic_with_a_warning()
    {
        _harness.Settings.ChangeExternally(s => s with { DefaultShellId = "wsl-ubuntu" });
        var terminal = new TerminalSettingsViewModel(_harness.CreateStore(), _harness.Shells);

        await terminal.ActivateAsync();

        terminal.SelectedShell!.Id.Should().BeNull();
        terminal.ShellWarning.Should().Contain("no longer installed");
        _harness.Settings.Current.DefaultShellId.Should().Be("wsl-ubuntu", "nothing changes until the user chooses");
    }

    [Theory]
    [InlineData(ShellKind.PowerShellCore, "WindowConsole20")]
    [InlineData(ShellKind.WindowsPowerShell, "WindowConsole20")]
    [InlineData(ShellKind.CommandPrompt, "AppGeneric20")]
    [InlineData(ShellKind.GitBash, "BranchFork20")]
    [InlineData(ShellKind.Wsl, "Code20")]
    [InlineData(ShellKind.Custom, "WindowDevTools20")]
    public void Each_kind_of_shell_has_an_icon(ShellKind kind, string icon)
    {
        ShellOption.IconFor(kind).Should().Be(icon);
    }

    [Fact]
    public void Terminal_font_size_is_validated()
    {
        var terminal = new TerminalSettingsViewModel(_harness.CreateStore(), _harness.Shells);

        terminal.TerminalFontSize = 64;

        terminal.HasTerminalFontSizeError.Should().BeTrue();
        _harness.Settings.Current.TerminalFontSize.Should().Be(AppSettings.Default.TerminalFontSize);

        terminal.TerminalFontSize = 15;

        _harness.Settings.Current.TerminalFontSize.Should().Be(15);
    }

    [Theory]
    [InlineData(10.0, null)]
    [InlineData(5000.0, null)]
    [InlineData(9.0, "Use a value between 10 and 5000 runs.")]
    [InlineData(12.5, "Use a whole number.")]
    public void Run_history_is_validated(double value, string? error)
    {
        var commands = new CommandsSettingsViewModel(_harness.CreateStore());

        commands.HistoryPerProject = value;

        commands.HistoryError.Should().Be(error);
        _harness.Settings.Current.RunHistoryPerProject.Should().Be(error is null ? (int)value : AppSettings.Default.RunHistoryPerProject);
    }

    [Fact]
    public void Command_notifications_and_stalled_warnings_are_saved()
    {
        var commands = new CommandsSettingsViewModel(_harness.CreateStore());

        commands.NotifyWhenFinished = false;
        commands.StalledMinutes = 15;
        commands.StalledMinutes = 0;

        _harness.Settings.Current.NotifyWhenRunCompletes.Should().BeFalse();
        _harness.Settings.Current.StalledRunWarningMinutes.Should().Be(15);
        commands.HasStalledError.Should().BeTrue();
    }

    [Fact]
    public async Task The_editor_command_is_saved_and_the_detected_editor_refreshed()
    {
        _harness.Shell.EditorName.Returns("Visual Studio Code", "subl");
        var editor = new EditorSettingsViewModel(_harness.CreateStore(), _harness.Shell, NullLogger.Instance);
        await editor.ActivateAsync();
        editor.EditorName.Should().Be("Visual Studio Code");

        editor.EditorCommand = "\"C:\\Tools\\subl.exe\" {file}:{line}";

        _harness.Settings.Current.ExternalEditorCommand.Should().Be("\"C:\\Tools\\subl.exe\" {file}:{line}");
        editor.EditorName.Should().Be("subl");

        editor.EditorCommand = "   ";

        _harness.Settings.Current.ExternalEditorCommand.Should().BeNull();
    }

    [Theory]
    [InlineData("code", null)]
    [InlineData("\"C:\\Program Files\\Editor\\edit.exe\" {file}", null)]
    [InlineData("\"C:\\Program Files\\Editor\\edit.exe {file}", "Close the quotes around the program path.")]
    [InlineData("{file}", "Start with the program to run, then its arguments.")]
    public void Editor_commands_are_validated(string command, string? error)
    {
        EditorSettingsViewModel.ValidateCommand(command).Should().Be(error);
    }

    [Fact]
    public void No_editor_found_is_explained()
    {
        var editor = new EditorSettingsViewModel(_harness.CreateStore(), _harness.Shell, NullLogger.Instance);

        editor.HasEditor.Should().BeFalse();
        editor.EditorText.Should().Contain("No editor found");
    }
}
