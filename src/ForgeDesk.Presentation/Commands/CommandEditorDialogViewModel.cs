using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Commands;

/// <summary>"Add command" / "Edit command": name, command line, category and working directory (relative to the project).</summary>
public sealed partial class CommandEditorDialogViewModel : ObservableObject, IDialogViewModel
{
    public const int MaxNameLength = 200;
    public const int MaxCommandLineLength = 8000;

    private readonly string _projectRoot;

    public CommandEditorDialogViewModel(string projectRoot, DetectedCommand? existing = null, CommandCategory? category = null)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        _projectRoot = projectRoot;
        IsEditing = existing is not null;
        Name = existing?.Name ?? string.Empty;
        CommandLine = existing?.CommandLine ?? string.Empty;
        WorkingDirectory = existing?.WorkingDirectory ?? string.Empty;
        var selected = existing?.Category ?? category ?? CommandCategory.Other;
        SelectedCategory = CommandCategories.Options.First(o => o.Category == selected);
    }

    public string Title => IsEditing ? "Edit command" : "Add command";

    public double PreferredWidth => 520;

    public double? PreferredHeight => null;

    public bool IsEditing { get; }

    public string ConfirmText => IsEditing ? "Save" : "Add command";

    public string Explanation => "Runs in a shell from the working directory, with its output, progress and history in the Commands tab.";

    public IReadOnlyList<CommandCategoryOption> Categories => CommandCategories.Options;

    public event EventHandler<bool?>? CloseRequested;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string CommandLine { get; set; }

    [ObservableProperty]
    public partial CommandCategoryOption SelectedCategory { get; set; }

    /// <summary>Relative to the project root; empty runs from the root.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkingDirectoryHint))]
    public partial string WorkingDirectory { get; set; }

    [ObservableProperty]
    public partial string? NameError { get; private set; }

    [ObservableProperty]
    public partial string? CommandLineError { get; private set; }

    [ObservableProperty]
    public partial string? WorkingDirectoryError { get; private set; }

    public string WorkingDirectoryHint
    {
        get
        {
            var normalized = NormalizeWorkingDirectory(WorkingDirectory, out _);
            return string.IsNullOrEmpty(normalized) ? "Runs from the project root." : $"Runs from {normalized}.";
        }
    }

    /// <summary>The validated command line (trimmed).</summary>
    public string ResultCommandLine => CommandLine.Trim();

    public string ResultName => Name.Trim();

    /// <summary>The validated directory relative to the project, null for the root.</summary>
    public string? ResultWorkingDirectory => NormalizeWorkingDirectory(WorkingDirectory, out _) is { Length: > 0 } dir ? dir : null;

    public CommandCategory ResultCategory => SelectedCategory.Category;

    partial void OnNameChanged(string value) => NameError = null;

    partial void OnCommandLineChanged(string value) => CommandLineError = null;

    partial void OnWorkingDirectoryChanged(string value) => WorkingDirectoryError = null;

    [RelayCommand]
    private void Confirm()
    {
        if (Validate())
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>Validates every field and sets their messages; true when the form can be saved.</summary>
    public bool Validate()
    {
        var name = Name.Trim();
        NameError = name.Length == 0 ? "Give the command a name, for example \"Start API\"."
            : name.Length > MaxNameLength ? $"Use at most {MaxNameLength} characters."
            : null;

        var commandLine = CommandLine.Trim();
        CommandLineError = commandLine.Length == 0 ? "Enter the command to run, for example \"npm run dev\"."
            : commandLine.Length > MaxCommandLineLength ? $"Use at most {MaxCommandLineLength:N0} characters, or put the steps in a script."
            : commandLine.Contains('\n', StringComparison.Ordinal) || commandLine.Contains('\r', StringComparison.Ordinal)
                ? "The command must fit on one line. Chain steps with &&."
            : null;

        NormalizeWorkingDirectory(WorkingDirectory, out var directoryError);
        WorkingDirectoryError = directoryError;
        if (directoryError is null && ResultWorkingDirectory is { } relative)
        {
            try
            {
                var full = PathUtil.ResolveUnder(_projectRoot, relative);
                if (!Directory.Exists(full))
                {
                    WorkingDirectoryError = $"The folder \"{relative}\" doesn't exist in the project.";
                }
            }
            catch (Exception ex) when (ex is ForgeException or ArgumentException or IOException or NotSupportedException)
            {
                WorkingDirectoryError = "The working directory must be a folder inside the project.";
            }
        }

        return NameError is null && CommandLineError is null && WorkingDirectoryError is null;
    }

    /// <summary>"./src\web/" → "src/web"; "" → ""; rejects absolute paths and "..".</summary>
    internal static string NormalizeWorkingDirectory(string? value, out string? error)
    {
        error = null;
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var normalized = trimmed.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(trimmed))
        {
            error = "Use a folder relative to the project, such as \"src/web\".";
            return string.Empty;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (segments.Any(s => s == ".."))
        {
            error = "The working directory must be inside the project (no \"..\").";
            return string.Empty;
        }

        if (segments.Any(s => s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.IndexOfAny(['<', '>', '|', '"', '*', '?']) >= 0))
        {
            error = "The folder name contains characters Windows doesn't allow.";
            return string.Empty;
        }

        return string.Join('/', segments);
    }
}
