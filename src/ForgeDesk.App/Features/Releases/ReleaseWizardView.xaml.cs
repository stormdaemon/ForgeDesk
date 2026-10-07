using System.Windows.Controls;
using System.Windows.Input;
using ForgeDesk.Presentation.Releases;

namespace ForgeDesk.App.Features.Releases;

/// <summary>
/// The "New release" wizard. View-only behavior: focusing the custom version box selects the custom
/// version, Delete removes the selected asset.
/// </summary>
public partial class ReleaseWizardView : UserControl
{
    public ReleaseWizardView()
    {
        InitializeComponent();
    }

    private void OnCustomVersionFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is ReleaseWizardViewModel { UseCustomVersion: false } wizard && wizard.ChooseCustomVersionCommand.CanExecute(null))
        {
            wizard.ChooseCustomVersionCommand.Execute(null);
        }
    }

    private void OnAssetListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
            && DataContext is ReleaseWizardViewModel wizard
            && sender is ListBox { SelectedItem: ReleaseAssetFileViewModel asset })
        {
            wizard.RemoveAssetCommand.Execute(asset);
            e.Handled = true;
        }
    }
}
