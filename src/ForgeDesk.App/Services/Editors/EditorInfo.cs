namespace ForgeDesk.App.Services.Editors;

/// <summary>How an editor expects "open this file at this line" to be spelled.</summary>
internal enum EditorFamily
{
    /// <summary>Visual Studio Code and its forks (Cursor, VSCodium): <c>-g file:line</c>.</summary>
    VisualStudioCode,

    /// <summary>Notepad++: <c>-nLINE file</c>.</summary>
    NotepadPlusPlus,

    /// <summary>Windows Notepad: file only.</summary>
    Notepad,

    /// <summary>A command configured by the user in Settings (<c>ExternalEditorCommand</c>).</summary>
    Custom,
}

/// <summary>A code editor ForgeDesk can launch.</summary>
/// <param name="Name">Display name ("Visual Studio Code").</param>
/// <param name="Family">Argument conventions.</param>
/// <param name="Executable">Absolute path of the program (.exe, or a .cmd shim).</param>
/// <param name="ArgumentTemplate">For custom editors, the argument template with {file}/{line} placeholders.</param>
internal sealed record EditorInfo(string Name, EditorFamily Family, string Executable, string? ArgumentTemplate = null)
{
    public bool CanOpenFolders => Family is EditorFamily.VisualStudioCode or EditorFamily.Custom;
}

/// <summary>What the editor locator needs from the machine (abstracted for tests).</summary>
internal interface IEditorEnvironment
{
    string? GetVariable(string name);

    bool FileExists(string path);

    /// <summary>Finds an executable in PATH (name includes its extension).</summary>
    string? FindInPath(string fileName);
}
