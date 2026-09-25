using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ForgeDesk.App.Services;
using ForgeDesk.Core.Common;

namespace ForgeDesk.App.Controls;

/// <summary>
/// Inline error state bound to <c>ViewModelBase.Error</c>: icon, title, message, hint, an
/// expandable "Technical details" section with a copy button, and <see cref="RetryCommand"/>.
/// Collapses itself while <see cref="Error"/> is null, so it can sit on top of any view.
/// </summary>
public partial class ErrorPanel : UserControl
{
    public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(
        nameof(Error), typeof(ErrorInfo), typeof(ErrorPanel), new PropertyMetadata(null, OnErrorChanged));

    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(ErrorPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty RetryTextProperty = DependencyProperty.Register(
        nameof(RetryText), typeof(string), typeof(ErrorPanel), new PropertyMetadata("Retry"));

    private readonly DispatcherTimer _copyFeedbackTimer;

    public ErrorPanel()
    {
        InitializeComponent();
        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyButton.Content = "Copy details";
        };
    }

    public ErrorInfo? Error
    {
        get => (ErrorInfo?)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public string RetryText
    {
        get => (string)GetValue(RetryTextProperty);
        set => SetValue(RetryTextProperty, value);
    }

    private static void OnErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ErrorPanel)d).Render(e.NewValue as ErrorInfo);

    private void Render(ErrorInfo? error)
    {
        if (error is null)
        {
            Panel.Visibility = Visibility.Collapsed;
            return;
        }

        KindIcon.Symbol = ErrorSymbols.For(error.Kind);
        TitleText.Text = error.Title;
        MessageText.Text = error.Message;
        HintText.Text = error.Hint ?? string.Empty;
        HintRow.Visibility = string.IsNullOrWhiteSpace(error.Hint) ? Visibility.Collapsed : Visibility.Visible;
        DetailText.Text = error.Detail ?? string.Empty;
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(error.Detail) ? Visibility.Collapsed : Visibility.Visible;
        DetailsExpander.IsExpanded = false;
        Panel.Visibility = Visibility.Visible;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (Error is not { } error)
        {
            return;
        }

        try
        {
            ClipboardWriter.SetText(ErrorInfoText.Format(error));
            CopyButton.Content = "Copied";
        }
        catch (ForgeException)
        {
            CopyButton.Content = "Clipboard busy — try again";
        }

        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }
}
