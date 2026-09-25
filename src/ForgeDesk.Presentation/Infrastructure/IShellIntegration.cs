namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>Operating-system integration (Explorer, browser, editors, clipboard).</summary>
public interface IShellIntegration
{
    void OpenFolder(string path);

    /// <summary>Opens Explorer with the file selected.</summary>
    void RevealInExplorer(string path);

    /// <summary>Opens the file with its default application.</summary>
    void OpenWithDefaultApp(string path);

    /// <summary>Opens a file (optionally at a line) in the configured or detected code editor.</summary>
    void OpenInEditor(string path, int? line = null);

    /// <summary>Opens a folder in the code editor (VS Code, Visual Studio, …).</summary>
    void OpenFolderInEditor(string path);

    /// <summary>Name of the detected editor ("Visual Studio Code"), or null.</summary>
    string? EditorName { get; }

    void OpenExternalTerminal(string directory);

    void OpenUrl(string url);

    void CopyToClipboard(string text);
}
