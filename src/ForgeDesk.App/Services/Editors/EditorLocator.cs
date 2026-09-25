using System.IO;

namespace ForgeDesk.App.Services.Editors;

/// <summary>
/// Finds the code editor to use: the command configured in Settings, then Visual Studio Code,
/// Cursor, VSCodium, Notepad++ and finally Notepad.
/// </summary>
internal sealed class EditorLocator
{
    private const string VisualStudioCodeName = "Visual Studio Code";
    private const string CursorName = "Cursor";
    private const string VSCodiumName = "VSCodium";
    private const string NotepadPlusPlusName = "Notepad++";
    private const string NotepadName = "Notepad";

    private readonly IEditorEnvironment _environment;

    public EditorLocator(IEditorEnvironment environment) => _environment = environment;

    /// <summary>Returns the editor to use, or null when none was found.</summary>
    public EditorInfo? Locate(string? configuredCommand)
    {
        if (!string.IsNullOrWhiteSpace(configuredCommand) && FromConfiguredCommand(configuredCommand) is { } configured)
        {
            return configured;
        }

        return FindVsCodeFamily(VisualStudioCodeName, "code.cmd", "Code.exe",
                   LocalPrograms("Microsoft VS Code"), ProgramFiles("Microsoft VS Code"))
            ?? FindVsCodeFamily(CursorName, "cursor.cmd", "Cursor.exe", LocalPrograms("cursor"))
            ?? FindVsCodeFamily(VSCodiumName, "codium.cmd", "VSCodium.exe", LocalPrograms("VSCodium"), ProgramFiles("VSCodium"))
            ?? FindNotepadPlusPlus()
            ?? FindNotepad();
    }

    /// <summary>
    /// Parses the ExternalEditorCommand setting: an executable (quoted when it contains spaces,
    /// or a bare path) optionally followed by an argument template using {file} and {line}.
    /// </summary>
    internal EditorInfo? FromConfiguredCommand(string command)
    {
        var trimmed = command.Trim();
        string executable;
        string template;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            executable = trimmed[1..end];
            template = trimmed[(end + 1)..].Trim();
        }
        else if (Path.IsPathRooted(trimmed) && _environment.FileExists(trimmed))
        {
            // An unquoted path such as C:\Program Files\Sublime Text\subl.exe.
            executable = trimmed;
            template = string.Empty;
        }
        else
        {
            var space = trimmed.IndexOfAny([' ', '\t']);
            executable = space < 0 ? trimmed : trimmed[..space];
            template = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        }

        var resolved = ResolveExecutable(executable);
        if (resolved is null)
        {
            return null;
        }

        if (template.Length == 0 && KnownEditor(resolved) is { } known)
        {
            return known;
        }

        return new EditorInfo(Path.GetFileNameWithoutExtension(resolved), EditorFamily.Custom, resolved, template);
    }

    private string? ResolveExecutable(string executable)
    {
        if (executable.Length == 0)
        {
            return null;
        }

        if (Path.IsPathRooted(executable))
        {
            return _environment.FileExists(executable) ? executable : null;
        }

        return _environment.FindInPath(executable);
    }

    private static EditorInfo? KnownEditor(string executable)
    {
        var fileName = Path.GetFileName(executable).ToUpperInvariant();
        return fileName switch
        {
            "CODE.EXE" or "CODE.CMD" => new EditorInfo(VisualStudioCodeName, EditorFamily.VisualStudioCode, executable),
            "CURSOR.EXE" or "CURSOR.CMD" => new EditorInfo(CursorName, EditorFamily.VisualStudioCode, executable),
            "VSCODIUM.EXE" or "CODIUM.CMD" => new EditorInfo(VSCodiumName, EditorFamily.VisualStudioCode, executable),
            "NOTEPAD++.EXE" => new EditorInfo(NotepadPlusPlusName, EditorFamily.NotepadPlusPlus, executable),
            "NOTEPAD.EXE" => new EditorInfo(NotepadName, EditorFamily.Notepad, executable),
            _ => null,
        };
    }

    private EditorInfo? FindVsCodeFamily(string name, string shimName, string executableName, params string?[] installDirectories)
    {
        // The CLI shim in PATH (…\bin\code.cmd) tells us where the user's preferred install lives;
        // launching the .exe next to it avoids a cmd.exe hop.
        if (_environment.FindInPath(shimName) is { } shim)
        {
            var executable = FindNearShim(shim, executableName) ?? shim;
            return new EditorInfo(name, EditorFamily.VisualStudioCode, executable);
        }

        foreach (var directory in installDirectories)
        {
            if (directory is null)
            {
                continue;
            }

            var candidate = Path.Combine(directory, executableName);
            if (_environment.FileExists(candidate))
            {
                return new EditorInfo(name, EditorFamily.VisualStudioCode, candidate);
            }
        }

        return null;
    }

    private string? FindNearShim(string shim, string executableName)
    {
        var directory = Path.GetDirectoryName(shim);
        for (var level = 0; level < 4 && !string.IsNullOrEmpty(directory); level++)
        {
            var candidate = Path.Combine(directory, executableName);
            if (_environment.FileExists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private EditorInfo? FindNotepadPlusPlus()
    {
        foreach (var directory in new[] { ProgramFiles("Notepad++"), ProgramFilesX86("Notepad++") })
        {
            if (directory is not null && Path.Combine(directory, "notepad++.exe") is var candidate && _environment.FileExists(candidate))
            {
                return new EditorInfo(NotepadPlusPlusName, EditorFamily.NotepadPlusPlus, candidate);
            }
        }

        return _environment.FindInPath("notepad++.exe") is { } inPath
            ? new EditorInfo(NotepadPlusPlusName, EditorFamily.NotepadPlusPlus, inPath)
            : null;
    }

    private EditorInfo? FindNotepad()
    {
        if (_environment.GetVariable("SystemRoot") is { Length: > 0 } systemRoot)
        {
            var candidate = Path.Combine(systemRoot, "System32", "notepad.exe");
            if (_environment.FileExists(candidate))
            {
                return new EditorInfo(NotepadName, EditorFamily.Notepad, candidate);
            }
        }

        return _environment.FindInPath("notepad.exe") is { } inPath
            ? new EditorInfo(NotepadName, EditorFamily.Notepad, inPath)
            : null;
    }

    private string? LocalPrograms(string folder) =>
        _environment.GetVariable("LOCALAPPDATA") is { Length: > 0 } local ? Path.Combine(local, "Programs", folder) : null;

    private string? ProgramFiles(string folder) =>
        _environment.GetVariable("ProgramFiles") is { Length: > 0 } programFiles ? Path.Combine(programFiles, folder) : null;

    private string? ProgramFilesX86(string folder) =>
        _environment.GetVariable("ProgramFiles(x86)") is { Length: > 0 } programFiles ? Path.Combine(programFiles, folder) : null;
}
