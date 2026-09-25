using System.Windows;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Dialogs;

/// <summary>
/// Modal window hosting any <see cref="IDialogViewModel"/>; its view is resolved by DataTemplate.
/// Sized from PreferredWidth/PreferredHeight, closed by the view model's CloseRequested or Esc.
/// </summary>
public partial class DialogWindow : ForgeDialogWindow
{
    public DialogWindow(IDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(320, viewModel.PreferredWidth), workArea.Width * 0.95);
        MaxHeight = workArea.Height * 0.9;
        if (viewModel.PreferredHeight is { } height && height > 0)
        {
            SizeToContent = SizeToContent.Manual;
            Height = Math.Min(height, MaxHeight);
            ResizeMode = ResizeMode.CanResize;
            MinWidth = Math.Min(Width, 400);
            MinHeight = Math.Min(Height, 240);
        }

        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    /// <summary>The value passed to CloseRequested; null when the user dismissed the dialog.</summary>
    public bool? Result { get; private set; }

    private void OnCloseRequested(object? sender, bool? result)
    {
        // View models may raise the event from a background continuation.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnCloseRequested(sender, result));
            return;
        }

        Result = result;
        if (IsVisible)
        {
            Close();
        }
    }
}
