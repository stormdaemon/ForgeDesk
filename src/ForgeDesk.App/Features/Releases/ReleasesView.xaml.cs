using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.Releases;

namespace ForgeDesk.App.Features.Releases;

/// <summary>
/// The Releases tab: release cards (assets, expandable notes), the unreleased-tags hint and the
/// "New release" wizard shown in place of the list. View-only behavior: Enter opens the focused
/// release on GitHub, Space shows or hides its notes.
/// </summary>
public partial class ReleasesView : UserControl
{
    public ReleasesView()
    {
        InitializeComponent();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || DataContext is not ReleasesViewModel releases
            || (e.OriginalSource as ListBoxItem)?.DataContext is not ReleaseRowViewModel row)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            releases.OpenReleaseCommand.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            releases.ToggleNotesCommand.Execute(row);
            e.Handled = true;
        }
    }
}
