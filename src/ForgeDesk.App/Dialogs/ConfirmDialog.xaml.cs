using System.Windows;
using ForgeDesk.Presentation.Infrastructure;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Dialogs;

/// <summary>
/// Asks the user to confirm an action. Destructive confirmations get a red button that names
/// the action, and Enter maps to Cancel so a stray keypress never destroys anything.
/// </summary>
public partial class ConfirmDialog : ForgeDialogWindow
{
    private bool _confirmed;

    public ConfirmDialog(ConfirmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();

        Title = options.Title;
        HeadingText.Text = options.Title;
        MessageText.Text = options.Message;
        ConfirmButton.Content = options.ConfirmText;
        CancelButton.Content = options.CancelText;

        if (options.IsDestructive)
        {
            ConfirmButton.Appearance = ControlAppearance.Danger;
            CancelButton.IsDefault = true;
        }
        else
        {
            ConfirmButton.IsDefault = true;
        }

        if (!string.IsNullOrWhiteSpace(options.CheckboxText))
        {
            Option.Content = options.CheckboxText;
            Option.IsChecked = options.CheckboxDefault;
            Option.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => (options.IsDestructive ? CancelButton : ConfirmButton).Focus();
    }

    public ConfirmResult Result => new(_confirmed, _confirmed && Option.IsChecked == true);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
