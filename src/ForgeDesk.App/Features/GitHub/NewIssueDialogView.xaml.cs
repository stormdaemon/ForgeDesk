using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>"New issue" dialog: title, Markdown description with preview. The title box has the focus when it opens.</summary>
public partial class NewIssueDialogView : UserControl
{
    public NewIssueDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Dispatcher.BeginInvoke(() => Keyboard.Focus(TitleBox), System.Windows.Threading.DispatcherPriority.Input);
    }
}
