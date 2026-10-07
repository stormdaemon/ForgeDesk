using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>"New pull request" dialog: branches, title, Markdown description with preview, draft. The title box has the focus when it opens.</summary>
public partial class NewPullRequestDialogView : UserControl
{
    public NewPullRequestDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(TitleBox);
            TitleBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
