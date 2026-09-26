using System.Diagnostics;
using System.IO;
using ForgeDesk.App.Services.Editors;
using ForgeDesk.App.Services.Launching;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.App.Services;

/// <summary>Explorer, browser, code editor, external terminal and clipboard integration.</summary>
internal sealed class ShellIntegration : IShellIntegration
{
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ShellIntegration> _logger;
    private readonly EditorLocator _editorLocator;
    private readonly IEditorEnvironment _environment;
    private readonly Lock _gate = new();
    private EditorInfo? _editor;
    private string? _editorCommand;
    private bool _editorResolved;

    public ShellIntegration(ISettingsService settings, IUiDispatcher dispatcher, ILogger<ShellIntegration> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
        _environment = new SystemEditorEnvironment();
        _editorLocator = new EditorLocator(_environment);
    }

    public string? EditorName => ResolveEditor()?.Name;

    public void OpenFolder(string path)
    {
        var folder = RequireDirectory(path);
        Launch(new LaunchCommand(ExplorerPath, WindowsCommandLine.Quote(folder)));
    }

    public void RevealInExplorer(string path)
    {
        var target = RequireExisting(path);
        Launch(new LaunchCommand(ExplorerPath, "/select," + WindowsCommandLine.Quote(target)));
    }

    public void OpenWithDefaultApp(string path)
    {
        // Explorer resolves the file association exactly like a double-click would.
        var target = RequireExisting(path);
        Launch(new LaunchCommand(ExplorerPath, WindowsCommandLine.Quote(target)));
    }

    public void OpenInEditor(string path, int? line = null)
    {
        var file = RequireExisting(path);
        var editor = ResolveEditor() ?? throw NoEditor();
        var command = EditorCommandBuilder.OpenFile(editor, file, line, CommandProcessorPath);
        Launch(command, Path.GetDirectoryName(file));
    }

    public void OpenFolderInEditor(string path)
    {
        var folder = RequireDirectory(path);
        var editor = ResolveEditor() ?? throw NoEditor();
        var command = EditorCommandBuilder.OpenFolder(editor, folder, CommandProcessorPath)
            ?? throw new ForgeException(ErrorKind.ToolNotFound, $"{editor.Name} cannot open folders.",
                "Install Visual Studio Code, or choose an editor that can open folders in Settings.");
        Launch(command, folder);
    }

    public void OpenExternalTerminal(string directory)
    {
        var folder = RequireDirectory(directory);
        if (_environment.FindInPath("wt.exe") is { } windowsTerminal)
        {
            // Windows Terminal treats ';' as a command separator unless escaped.
            Launch(LaunchCommand.For(windowsTerminal, "-d", folder.Replace(";", "\\;", StringComparison.Ordinal)), folder);
            return;
        }

        var shell = _environment.FindInPath("pwsh.exe") ?? WindowsPowerShellPath;
        if (File.Exists(shell))
        {
            Launch(LaunchCommand.For(shell, "-NoLogo"), folder, newConsole: true);
            return;
        }

        Launch(new LaunchCommand(CommandProcessorPath, string.Empty), folder, newConsole: true);
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Only web links (http or https) can be opened.", null, url);
        }

        Launch(new LaunchCommand(ExplorerPath, WindowsCommandLine.Quote(uri.AbsoluteUri)));
    }

    public void CopyToClipboard(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_dispatcher.CheckAccess())
        {
            ClipboardWriter.SetText(text);
        }
        else
        {
            _dispatcher.InvokeAsync(() => ClipboardWriter.SetText(text)).GetAwaiter().GetResult();
        }
    }

    private static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string ExplorerPath => Path.Combine(WindowsDirectory, "explorer.exe");

    private static string WindowsPowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string CommandProcessorPath
    {
        get
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            return !string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec)
                ? comspec
                : Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }
    }

    private EditorInfo? ResolveEditor()
    {
        var configured = _settings.Current.ExternalEditorCommand;
        lock (_gate)
        {
            if (_editorResolved && string.Equals(_editorCommand, configured, StringComparison.Ordinal))
            {
                return _editor;
            }

            EditorInfo? editor = null;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                editor = _editorLocator.FromConfiguredCommand(configured);
                if (editor is null)
                {
                    _logger.LogWarning("The editor command configured in Settings could not be found; detecting an installed editor instead");
                }
            }

            editor ??= _editorLocator.Locate(null);
            if (editor is { Family: EditorFamily.Custom })
            {
                editor = editor with { Name = ProductName(editor.Executable) ?? editor.Name };
            }

            _editor = editor;
            _editorCommand = configured;
            _editorResolved = true;
            return editor;
        }
    }

    private static string? ProductName(string executable)
    {
        try
        {
            var name = FileVersionInfo.GetVersionInfo(executable).ProductName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private void Launch(LaunchCommand command, string? workingDirectory = null, bool newConsole = false)
    {
        _logger.LogInformation("Launching {Program}", Path.GetFileName(command.Executable));
        DetachedProcessLauncher.Start(command, workingDirectory, newConsole);
    }

    private static ForgeException NoEditor() => new(ErrorKind.ToolNotFound, "No code editor was found.",
        "Install Visual Studio Code, or choose your editor in Settings.");

    private static string RequireExisting(string path)
    {
        var full = Normalize(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"'{Path.GetFileName(full)}' no longer exists.",
                "It may have been moved, renamed or deleted.", full);
        }

        return full;
    }

    private static string RequireDirectory(string path)
    {
        var full = Normalize(path);
        if (!Directory.Exists(full))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{full}' does not exist.",
                "It may have been moved, renamed or deleted.");
        }

        return full;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ForgeException.InvalidInput("No path was given.");
        }

        return PathUtil.Normalize(path);
    }

    private sealed class SystemEditorEnvironment : IEditorEnvironment
    {
        public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

        public bool FileExists(string path) => File.Exists(path);

        public string? FindInPath(string fileName) => ExecutableLocator.Find(fileName);
    }
}
