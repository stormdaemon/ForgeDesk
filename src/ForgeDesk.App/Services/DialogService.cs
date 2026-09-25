using System.IO;
using System.Windows;
using ForgeDesk.App.Dialogs;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Win32;

namespace ForgeDesk.App.Services;

/// <summary>
/// Modal dialogs (confirm, prompt, error, custom view models) and the Windows file and folder
/// pickers. Every dialog is owned by the active ForgeDesk window and runs on the UI thread.
/// </summary>
internal sealed class DialogService : IDialogService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppPaths _paths;
    private readonly IShellIntegration _shell;

    public DialogService(IUiDispatcher dispatcher, IAppPaths paths, IShellIntegration shell)
    {
        _dispatcher = dispatcher;
        _paths = paths;
        _shell = shell;
    }

    public async Task<bool> ConfirmAsync(ConfirmOptions options) =>
        (await ConfirmWithCheckboxAsync(options).ConfigureAwait(true)).Confirmed;

    public Task<ConfirmResult> ConfirmWithCheckboxAsync(ConfirmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _dispatcher.InvokeAsync(() =>
        {
            var dialog = new ConfirmDialog(options);
            dialog.ShowModal(ActiveWindow());
            return dialog.Result;
        });
    }

    public Task<string?> PromptAsync(PromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _dispatcher.InvokeAsync(() =>
        {
            var dialog = new PromptDialog(options);
            dialog.ShowModal(ActiveWindow());
            return dialog.Result;
        });
    }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) =>
        _dispatcher.InvokeAsync(() =>
        {
            var picker = new OpenFolderDialog { Title = title, Multiselect = false, ValidateNames = true };
            if (ExistingDirectory(initialDirectory) is { } directory)
            {
                picker.InitialDirectory = directory;
            }

            return ShowPicker(picker) ? picker.FolderName : null;
        });

    public Task<IReadOnlyList<string>> PickFilesAsync(string title, string? initialDirectory = null, bool allowMultiple = true) =>
        _dispatcher.InvokeAsync<IReadOnlyList<string>>(() =>
        {
            var picker = new OpenFileDialog
            {
                Title = title,
                Multiselect = allowMultiple,
                CheckFileExists = true,
                ValidateNames = true,
            };
            if (ExistingDirectory(initialDirectory) is { } directory)
            {
                picker.InitialDirectory = directory;
            }

            return ShowPicker(picker) ? picker.FileNames : [];
        });

    public Task ShowErrorAsync(ErrorInfo error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return _dispatcher.InvokeAsync(() =>
        {
            // Unexpected failures carry a stack trace: the log folder helps when reporting them.
            Action? openLogs = error.Kind == ErrorKind.Unknown && !string.IsNullOrWhiteSpace(error.Detail)
                ? () => _shell.OpenFolder(_paths.LogsDirectory)
                : null;
            new ErrorDialog(error, openLogs).ShowModal(ActiveWindow());
        });
    }

    public Task<bool?> ShowDialogAsync(IDialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return _dispatcher.InvokeAsync(() =>
        {
            var window = new DialogWindow(dialog);
            window.ShowModal(ActiveWindow());
            return window.Result;
        });
    }

    /// <summary>The window dialogs should belong to: the active one, else the main window.</summary>
    internal static Window? ActiveWindow()
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        var active = application.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        return active ?? (application.MainWindow is { IsVisible: true } main ? main : null);
    }

    private static bool ShowPicker(CommonDialog picker)
    {
        var owner = ActiveWindow();
        return (owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) == true;
    }

    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
