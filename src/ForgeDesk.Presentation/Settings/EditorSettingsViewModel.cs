using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Settings;

/// <summary>Settings › Editor: the detected code editor and an optional custom command.</summary>
public sealed partial class EditorSettingsViewModel : SettingsSectionViewModel
{
    public const string CommandExample = "\"C:\\Tools\\subl.exe\" {file}:{line}";

    private const string CommandKey = "editor.command";

    private readonly SettingsStore _store;
    private readonly IShellIntegration _shell;
    private readonly ILogger _logger;

    internal EditorSettingsViewModel(SettingsStore store, IShellIntegration shell, ILogger logger)
        : base("Editor", "Editor", "Code20", "Which editor opens files and folders", "editor code vscode visual studio sublime open file line")
    {
        _store = store;
        _shell = shell;
        _logger = logger;
        ApplySettings(store.Current);
    }

    /// <summary>"Visual Studio Code", the configured program, or null when none is found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorText), nameof(HasEditor))]
    public partial string? EditorName { get; private set; }

    public bool HasEditor => EditorName is not null;

    public string EditorText => EditorName ?? "No editor found. Install Visual Studio Code, or enter the command of your editor below.";

    [ObservableProperty]
    public partial string EditorCommand { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditorCommandError))]
    public partial string? EditorCommandError { get; private set; }

    public bool HasEditorCommandError => EditorCommandError is not null;

    protected override Task LoadAsync()
    {
        RefreshEditorName();
        return Task.CompletedTask;
    }

    protected override Task OnReactivatedAsync()
    {
        RefreshEditorName();
        return Task.CompletedTask;
    }

    internal static string? ValidateCommand(string? command)
    {
        var text = command?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Count(c => c == '"') % 2 != 0)
        {
            return "Close the quotes around the program path.";
        }

        return text.StartsWith("{", StringComparison.Ordinal) ? "Start with the program to run, then its arguments." : null;
    }

    partial void OnEditorCommandChanged(string value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        EditorCommandError = ValidateCommand(value);
        if (EditorCommandError is null)
        {
            var command = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _store.RunLater(CommandKey, async () =>
            {
                if (await _store.SaveAsync(s => s with { ExternalEditorCommand = command }).ConfigureAwait(true))
                {
                    RefreshEditorName();
                }
            });
        }
    }

    private void RefreshEditorName()
    {
        try
        {
            EditorName = _shell.EditorName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not detect the editor");
            EditorName = null;
        }
    }

    protected override void OnSettingsChanged(AppSettings settings)
    {
        if (!_store.IsPending(CommandKey))
        {
            EditorCommand = settings.ExternalEditorCommand ?? string.Empty;
            EditorCommandError = null;
        }
    }
}
