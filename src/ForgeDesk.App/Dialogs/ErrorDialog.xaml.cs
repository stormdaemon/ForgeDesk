using System.Windows;
using ForgeDesk.App.Controls;
using ForgeDesk.App.Services;
using ForgeDesk.Core.Common;

namespace ForgeDesk.App.Dialogs;

/// <summary>
/// Shows an <see cref="ErrorInfo"/>: what happened, what to do (hint), and the technical details
/// in a selectable box with "Copy details". Unexpected errors can offer "Open logs folder".
/// </summary>
public partial class ErrorDialog : ForgeDialogWindow
{
    private readonly ErrorInfo _error;
    private readonly Action? _openLogs;

    public ErrorDialog(ErrorInfo error, Action? openLogs = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        InitializeComponent();
        _error = error;
        _openLogs = openLogs;

        Title = error.Title;
        KindIcon.Symbol = ErrorSymbols.For(error.Kind);
        HeadingText.Text = error.Title;
        MessageText.Text = error.Message;
        HintText.Text = error.Hint ?? string.Empty;
        HintRow.Visibility = string.IsNullOrWhiteSpace(error.Hint) ? Visibility.Collapsed : Visibility.Visible;
        DetailText.Text = error.Detail ?? string.Empty;
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(error.Detail) ? Visibility.Collapsed : Visibility.Visible;
        LogsButton.Visibility = openLogs is null ? Visibility.Collapsed : Visibility.Visible;

        Loaded += (_, _) => CloseButton.Focus();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardWriter.SetText(ErrorInfoText.Format(_error));
            CopyButton.Content = "Copied";
        }
        catch (ForgeException ex)
        {
            CopyButton.Content = ex.Message;
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            _openLogs?.Invoke();
        }
        catch (ForgeException ex)
        {
            LogsButton.Content = ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
