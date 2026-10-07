using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ForgeDesk.Presentation.Dashboard;

namespace ForgeDesk.App.Features.Dialogs;

/// <summary>
/// "Clone a repository" dialog content. View-only plumbing: initial focus, and stopping a clone in
/// flight when the window is closed with Esc or its close button.
/// </summary>
public partial class CloneRepositoryView : UserControl
{
    private Window? _window;

    public CloneRepositoryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private CloneRepositoryDialogViewModel? ViewModel => DataContext as CloneRepositoryDialogViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Closing += OnWindowClosing;
        }

        // After layout, so the visible tab's box can take focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ViewModel is { IsUrlSource: true })
            {
                UrlBox.Focus();
            }
            else if (RepositorySearch.IsVisible)
            {
                RepositorySearch.Focus();
            }
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Closing -= OnWindowClosing;
            _window = null;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e) => ViewModel?.OnDialogClosed();
}
