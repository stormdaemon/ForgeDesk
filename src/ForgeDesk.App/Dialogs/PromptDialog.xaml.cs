using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Dialogs;

/// <summary>
/// Asks for a line (or, with <c>Multiline</c>, a block) of text. The optional validator runs as
/// the user types once they have started editing; confirming with invalid input shows the
/// message instead of closing. Enter confirms (Ctrl+Enter when multiline), Esc cancels.
/// </summary>
public partial class PromptDialog : ForgeDialogWindow
{
    private readonly PromptOptions _options;

    public PromptDialog(PromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();
        _options = options;

        Title = options.Title;
        HeadingText.Text = options.Title;
        if (!string.IsNullOrWhiteSpace(options.Message))
        {
            MessageText.Text = options.Message;
            MessageText.Visibility = Visibility.Visible;
        }

        ConfirmButton.Content = options.ConfirmText;
        Input.PlaceholderText = options.Placeholder ?? string.Empty;
        Input.Text = options.InitialValue ?? string.Empty;
        if (options.Multiline)
        {
            Input.AcceptsReturn = true;
            Input.TextWrapping = TextWrapping.Wrap;
            Input.MinHeight = 120;
            Input.MaxHeight = 320;
            Input.VerticalContentAlignment = VerticalAlignment.Top;
            Input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        else
        {
            ConfirmButton.IsDefault = true;
        }

        // Subscribed after the initial value is set, so that validation waits for a real edit.
        Input.TextChanged += OnTextChanged;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>The confirmed text, or null when the prompt was cancelled.</summary>
    public string? Result { get; private set; }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_options.Multiline && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            TryConfirm();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Validate(showErrors: true);

    private void OnConfirm(object sender, RoutedEventArgs e) => TryConfirm();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void TryConfirm()
    {
        if (!Validate(showErrors: true))
        {
            Input.Focus();
            return;
        }

        Result = Input.Text;
        Close();
    }

    private bool Validate(bool showErrors)
    {
        var error = _options.Validate?.Invoke(Input.Text);
        var valid = string.IsNullOrEmpty(error);
        ValidationText.Text = error ?? string.Empty;
        ValidationText.Visibility = !valid && showErrors ? Visibility.Visible : Visibility.Collapsed;
        return valid;
    }
}
