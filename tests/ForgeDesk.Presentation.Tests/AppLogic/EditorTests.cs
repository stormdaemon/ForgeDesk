using ForgeDesk.App.Services.Editors;
using ForgeDesk.App.Services.Launching;

namespace ForgeDesk.Presentation.Tests.AppLogic;

public class EditorCommandBuilderTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "forgedesk-editor-tests"));
    private static readonly string File = Path.Combine(Root, "My Project", "src", "Program.cs");
    private static readonly string Cmd = Path.Combine(Root, "Windows", "System32", "cmd.exe");

    private static EditorInfo Editor(EditorFamily family, string executable = "editor.exe", string? template = null) =>
        new("Editor", family, Path.Combine(Root, executable), template);

    [Fact]
    public void Vs_code_goes_to_the_line_with_g()
    {
        var command = EditorCommandBuilder.OpenFile(Editor(EditorFamily.VisualStudioCode, "Code.exe"), File, 42, Cmd);

        WindowsCommandLine.Split(command.Arguments).Should().Equal("-g", File + ":42");
        command.Executable.Should().EndWith("Code.exe");
    }

    [Fact]
    public void Vs_code_without_line_opens_the_file() =>
        WindowsCommandLine.Split(EditorCommandBuilder.OpenFile(Editor(EditorFamily.VisualStudioCode), File, null, Cmd).Arguments)
            .Should().Equal(File);

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Invalid_lines_are_ignored(int line) =>
        WindowsCommandLine.Split(EditorCommandBuilder.OpenFile(Editor(EditorFamily.VisualStudioCode), File, line, Cmd).Arguments)
            .Should().Equal(File);

    [Fact]
    public void Notepad_plus_plus_uses_n_switch() =>
        WindowsCommandLine.Split(EditorCommandBuilder.OpenFile(Editor(EditorFamily.NotepadPlusPlus), File, 7, Cmd).Arguments)
            .Should().Equal("-n7", File);

    [Fact]
    public void Notepad_only_gets_the_file() =>
        WindowsCommandLine.Split(EditorCommandBuilder.OpenFile(Editor(EditorFamily.Notepad), File, 7, Cmd).Arguments)
            .Should().Equal(File);

    [Fact]
    public void Custom_template_expands_and_requotes_placeholders()
    {
        var editor = Editor(EditorFamily.Custom, template: "--wait {file}:{line}:{column}");

        WindowsCommandLine.Split(EditorCommandBuilder.OpenFile(editor, File, 12, Cmd).Arguments)
            .Should().Equal("--wait", File + ":12:1");
    }

    [Fact]
    public void Custom_template_accepts_path_alias_and_user_quotes()
    {
        var editor = Editor(EditorFamily.Custom, template: "-line {line} \"{path}\"");

        WindowsCommandLine.Split(EditorCommandBuilder.OpenFile(editor, File, 3, Cmd).Arguments)
            .Should().Equal("-line", "3", File);
    }

    [Fact]
    public void Custom_template_without_placeholder_appends_the_path() =>
        WindowsCommandLine.Split(EditorCommandBuilder.OpenFile(Editor(EditorFamily.Custom, template: "--new-window"), File, 3, Cmd).Arguments)
            .Should().Equal("--new-window", File);

    [Fact]
    public void Custom_template_for_a_folder_drops_line_arguments()
    {
        var editor = Editor(EditorFamily.Custom, template: "-n{line} {file}:{line}");

        var command = EditorCommandBuilder.OpenFolder(editor, Root, Cmd);

        command.Should().NotBeNull();
        WindowsCommandLine.Split(command!.Arguments).Should().Equal(Root);
    }

    [Fact]
    public void Vs_code_opens_folders_but_notepad_cannot()
    {
        EditorCommandBuilder.OpenFolder(Editor(EditorFamily.VisualStudioCode), Root, Cmd).Should().NotBeNull();
        EditorCommandBuilder.OpenFolder(Editor(EditorFamily.Notepad), Root, Cmd).Should().BeNull();
        EditorCommandBuilder.OpenFolder(Editor(EditorFamily.NotepadPlusPlus), Root, Cmd).Should().BeNull();
    }

    [Fact]
    public void Cmd_shims_run_through_the_command_processor()
    {
        var command = EditorCommandBuilder.OpenFile(Editor(EditorFamily.VisualStudioCode, "code.cmd"), File, 5, Cmd);

        command.Executable.Should().Be(Cmd);
        command.HideWindow.Should().BeTrue();
        command.Arguments.Should().StartWith("/d /s /c \"").And.Contain("code.cmd");
    }
}

public class EditorLocatorTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "forgedesk-locator-tests"));
    private static readonly string LocalAppData = Path.Combine(Root, "LocalAppData");
    private static readonly string ProgramFiles = Path.Combine(Root, "Program Files");
    private static readonly string SystemRoot = Path.Combine(Root, "Windows");

    private readonly FakeEnvironment _environment = new();

    public EditorLocatorTests()
    {
        _environment.Variables["LOCALAPPDATA"] = LocalAppData;
        _environment.Variables["ProgramFiles"] = ProgramFiles;
        _environment.Variables["SystemRoot"] = SystemRoot;
    }

    private EditorInfo? Locate(string? configured = null) => new EditorLocator(_environment).Locate(configured);

    [Fact]
    public void Nothing_installed_finds_nothing() => Locate().Should().BeNull();

    [Fact]
    public void Notepad_is_the_last_resort()
    {
        _environment.AddFile(Path.Combine(SystemRoot, "System32", "notepad.exe"));

        Locate().Should().BeEquivalentTo(new { Name = "Notepad", Family = EditorFamily.Notepad });
    }

    [Fact]
    public void Vs_code_shim_in_path_resolves_to_the_executable_next_to_it()
    {
        var install = Path.Combine(Root, "Tools", "VS Code");
        var shim = _environment.AddFile(Path.Combine(install, "bin", "code.cmd"));
        _environment.PathEntries["code.cmd"] = shim;
        var exe = _environment.AddFile(Path.Combine(install, "Code.exe"));
        _environment.AddFile(Path.Combine(SystemRoot, "System32", "notepad.exe"));

        var editor = Locate();

        editor.Should().BeEquivalentTo(new { Name = "Visual Studio Code", Family = EditorFamily.VisualStudioCode, Executable = exe });
    }

    [Fact]
    public void Vs_code_shim_without_executable_is_used_directly()
    {
        var shim = _environment.AddFile(Path.Combine(Root, "shims", "code.cmd"));
        _environment.PathEntries["code.cmd"] = shim;

        Locate()!.Executable.Should().Be(shim);
    }

    [Fact]
    public void Vs_code_user_install_is_found_without_path()
    {
        var exe = _environment.AddFile(Path.Combine(LocalAppData, "Programs", "Microsoft VS Code", "Code.exe"));

        Locate()!.Executable.Should().Be(exe);
    }

    [Fact]
    public void Vs_code_wins_over_cursor_and_notepad_plus_plus()
    {
        _environment.AddFile(Path.Combine(LocalAppData, "Programs", "cursor", "Cursor.exe"));
        _environment.AddFile(Path.Combine(ProgramFiles, "Notepad++", "notepad++.exe"));
        var code = _environment.AddFile(Path.Combine(ProgramFiles, "Microsoft VS Code", "Code.exe"));

        Locate()!.Executable.Should().Be(code);
    }

    [Fact]
    public void Cursor_shim_several_levels_deep_resolves_to_its_executable()
    {
        var install = Path.Combine(LocalAppData, "Programs", "cursor");
        _environment.PathEntries["cursor.cmd"] = _environment.AddFile(Path.Combine(install, "resources", "app", "bin", "cursor.cmd"));
        var exe = _environment.AddFile(Path.Combine(install, "Cursor.exe"));

        Locate().Should().BeEquivalentTo(new { Name = "Cursor", Executable = exe });
    }

    [Fact]
    public void Notepad_plus_plus_comes_before_notepad()
    {
        _environment.AddFile(Path.Combine(SystemRoot, "System32", "notepad.exe"));
        _environment.AddFile(Path.Combine(ProgramFiles, "Notepad++", "notepad++.exe"));

        Locate()!.Family.Should().Be(EditorFamily.NotepadPlusPlus);
    }

    [Fact]
    public void Configured_command_with_quoted_path_and_template_is_custom()
    {
        var sublime = _environment.AddFile(Path.Combine(ProgramFiles, "Sublime Text", "subl.exe"));
        _environment.AddFile(Path.Combine(ProgramFiles, "Microsoft VS Code", "Code.exe"));

        var editor = Locate($"\"{sublime}\" {{file}}:{{line}}");

        editor.Should().BeEquivalentTo(new { Family = EditorFamily.Custom, Executable = sublime, ArgumentTemplate = "{file}:{line}" });
    }

    [Fact]
    public void Configured_unquoted_path_with_spaces_is_accepted()
    {
        var sublime = _environment.AddFile(Path.Combine(ProgramFiles, "Sublime Text", "subl.exe"));

        Locate(sublime)!.Executable.Should().Be(sublime);
    }

    [Fact]
    public void Configured_name_is_resolved_through_path_and_recognized()
    {
        _environment.PathEntries["code"] = _environment.AddFile(Path.Combine(Root, "bin", "code.cmd"));

        Locate("code").Should().BeEquivalentTo(new { Name = "Visual Studio Code", Family = EditorFamily.VisualStudioCode });
    }

    [Fact]
    public void Missing_configured_editor_falls_back_to_detection()
    {
        var code = _environment.AddFile(Path.Combine(ProgramFiles, "Microsoft VS Code", "Code.exe"));

        new EditorLocator(_environment).FromConfiguredCommand("\"C:\\nowhere\\editor.exe\" {file}").Should().BeNull();
        Locate("\"C:\\nowhere\\editor.exe\" {file}")!.Executable.Should().Be(code);
    }

    private sealed class FakeEnvironment : IEditorEnvironment
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> PathEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string AddFile(string path)
        {
            _files.Add(path);
            return path;
        }

        public string? GetVariable(string name) => Variables.GetValueOrDefault(name);

        public bool FileExists(string path) => _files.Contains(path);

        public string? FindInPath(string fileName) => PathEntries.GetValueOrDefault(fileName);
    }
}
