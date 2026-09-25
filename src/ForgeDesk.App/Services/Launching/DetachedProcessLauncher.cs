using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using ForgeDesk.App.Native;
using ForgeDesk.Core.Common;

namespace ForgeDesk.App.Services.Launching;

/// <summary>
/// Starts programs the user keeps using after ForgeDesk exits (editor, Explorer, browser,
/// external terminal). ForgeDesk runs inside a kill-on-close job object so that commands die
/// with it; these launches break away from that job, otherwise closing ForgeDesk would also
/// close the user's VS Code or browser.
/// </summary>
internal static class DetachedProcessLauncher
{
    public static void Start(LaunchCommand command, string? workingDirectory = null, bool newConsole = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OperatingSystem.IsWindows())
        {
            throw new ForgeException(ErrorKind.ToolNotFound, "Opening external programs is only supported on Windows.");
        }

        var flags = NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateDefaultErrorMode;
        if (command.HideWindow)
        {
            flags |= NativeMethods.CreateNoWindow;
        }
        else if (newConsole)
        {
            flags |= NativeMethods.CreateNewConsole;
        }

        if (TryCreate(command, workingDirectory, flags | NativeMethods.CreateBreakawayFromJob, out var error))
        {
            return;
        }

        // An outer job (debugger, terminal, CI agent) may forbid breaking away: start inside it instead.
        if (error == NativeMethods.ErrorAccessDenied && TryCreate(command, workingDirectory, flags, out error))
        {
            return;
        }

        var name = Path.GetFileName(command.Executable);
        var detail = $"{command.CommandLine}{Environment.NewLine}{new Win32Exception(error).Message} (error {error})";
        throw error is 2 or 3
            ? new ForgeException(ErrorKind.ToolNotFound, $"'{name}' could not be found.", "Check that the program is still installed.", detail)
            : new ForgeException(ErrorKind.ProcessFailed, $"'{name}' could not be started.", null, detail);
    }

    private static bool TryCreate(LaunchCommand command, string? workingDirectory, uint flags, out int error)
    {
        // CreateProcessW may write into the command-line buffer, so it must be a private, terminated copy.
        var commandLine = (command.CommandLine + '\0').ToCharArray();
        var startupInfo = new NativeMethods.StartupInfo { Cb = Marshal.SizeOf<NativeMethods.StartupInfo>() };
        var directory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;

        if (!NativeMethods.CreateProcess(command.Executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, directory, ref startupInfo, out var info))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        using (new SafeKernelHandle(info.Process))
        using (new SafeKernelHandle(info.Thread))
        {
            error = 0;
            return true;
        }
    }
}
