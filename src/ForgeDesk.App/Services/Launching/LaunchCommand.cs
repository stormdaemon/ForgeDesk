using System.IO;

namespace ForgeDesk.App.Services.Launching;

/// <summary>An executable plus its pre-formatted Windows argument string.</summary>
/// <param name="Executable">Absolute path of the program to start.</param>
/// <param name="Arguments">Arguments already quoted with <see cref="WindowsCommandLine.Quote"/>.</param>
/// <param name="HideWindow">Start without a console window (used for cmd.exe running a script shim).</param>
internal sealed record LaunchCommand(string Executable, string Arguments, bool HideWindow = false)
{
    public string CommandLine => Arguments.Length == 0
        ? WindowsCommandLine.Quote(Executable)
        : $"{WindowsCommandLine.Quote(Executable)} {Arguments}";

    public static LaunchCommand For(string executable, params IEnumerable<string> arguments) =>
        new(executable, WindowsCommandLine.Join(arguments));

    public static bool IsScript(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Batch files cannot be started by CreateProcess directly: they run through cmd.exe.
    /// /d skips AutoRun, /s with outer quotes keeps the command verbatim, /c exits afterwards.
    /// </summary>
    public static LaunchCommand ForScript(string script, string arguments, string commandProcessor)
    {
        var command = arguments.Length == 0 ? WindowsCommandLine.Quote(script) : $"{WindowsCommandLine.Quote(script)} {arguments}";
        return new LaunchCommand(commandProcessor, $"/d /s /c \"{command}\"", HideWindow: true);
    }
}
