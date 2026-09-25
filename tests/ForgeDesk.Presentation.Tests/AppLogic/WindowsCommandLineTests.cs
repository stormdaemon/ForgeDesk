using ForgeDesk.App.Services.Launching;

namespace ForgeDesk.Presentation.Tests.AppLogic;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"C:\Program Files\app.exe", "\"C:\\Program Files\\app.exe\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData(@"C:\My Dir\", "\"C:\\My Dir\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    public void Quote_follows_msvcrt_rules(string argument, string expected) =>
        WindowsCommandLine.Quote(argument).Should().Be(expected);

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData(@"C:\My Dir\")]
    [InlineData(@"trailing\\")]
    [InlineData("quote\"inside")]
    [InlineData("backslash\\\"quote")]
    [InlineData(@"\\server\share\folder with space\")]
    [InlineData("-g C:\\src\\file.cs:12")]
    public void Split_reverses_quote(string argument)
    {
        var commandLine = WindowsCommandLine.Join(["first", argument, "last"]);

        WindowsCommandLine.Split(commandLine).Should().Equal("first", argument, "last");
    }

    [Fact]
    public void Split_handles_quoted_segments_inside_a_token() =>
        WindowsCommandLine.Split("\"C:\\a b\\file.txt\":12 -n5").Should().Equal(@"C:\a b\file.txt:12", "-n5");

    [Fact]
    public void Split_ignores_extra_whitespace() =>
        WindowsCommandLine.Split("  one \t two   ").Should().Equal("one", "two");

    [Fact]
    public void Script_launch_goes_through_cmd_with_verbatim_quoting()
    {
        var command = LaunchCommand.ForScript(@"C:\Tools\code.cmd", "-g \"C:\\a b\\x.cs:3\"", @"C:\Windows\System32\cmd.exe");

        command.Executable.Should().Be(@"C:\Windows\System32\cmd.exe");
        command.Arguments.Should().Be("/d /s /c \"C:\\Tools\\code.cmd -g \"C:\\a b\\x.cs:3\"\"");
        command.HideWindow.Should().BeTrue();
    }

    [Theory]
    [InlineData("tool.cmd", true)]
    [InlineData("TOOL.BAT", true)]
    [InlineData("tool.exe", false)]
    [InlineData("tool", false)]
    public void Scripts_are_recognized_by_extension(string file, bool expected) =>
        LaunchCommand.IsScript(file).Should().Be(expected);

    [Fact]
    public void Command_line_quotes_the_executable()
    {
        var command = LaunchCommand.For(@"C:\Program Files\Editor\editor.exe", "file name.txt");

        command.CommandLine.Should().Be("\"C:\\Program Files\\Editor\\editor.exe\" \"file name.txt\"");
    }
}
