using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace ForgeDesk.App;

/// <summary>
/// The main window: Fluent chrome (Mica, extended title bar) around <c>ShellHost</c>, which
/// renders the shell view model set as DataContext, plus the snackbar host for notifications.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Where <see cref="Services.Notifications.NotificationService"/> shows snackbars.</summary>
    public SnackbarPresenter SnackbarPresenter => SnackbarHost;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The shell view brings its own title bar; two title bars would both hook the window's
        // hit-testing, so the fallback one leaves the tree for good.
        if (e.NewValue is not null && FallbackTitleBar.Parent is Panel panel)
        {
            panel.Children.Remove(FallbackTitleBar);
            DataContextChanged -= OnDataContextChanged;
        }
    }
}
