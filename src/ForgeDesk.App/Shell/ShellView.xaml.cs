using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ForgeDesk.Presentation.Shell;

namespace ForgeDesk.App.Shell;

/// <summary>
/// Main window content. Code-behind is limited to view behavior: starting the shell once it is on
/// screen, typing into the title-bar search field, and centering the palette under the title bar.
/// </summary>
public partial class ShellView : UserControl
{
    /// <summary>Distance from the top of the window to the palette card (just under the search field).</summary>
    private const double PaletteTop = 6;

    public ShellView()
    {
        InitializeComponent();
        PalettePopup.CustomPopupPlacementCallback = PlacePalette;
        Loaded += OnLoaded;
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Idempotent: the view can be reloaded (theme change), the shell starts once.
        _ = ViewModel?.InitializeAsync();
    }

    /// <summary>Typing while the search field has focus opens the palette with what was typed.</summary>
    private void OnSearchBoxTextInput(object sender, TextCompositionEventArgs e)
    {
        if (ViewModel is { } shell && !string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
        {
            shell.Palette.Open(e.Text);
            e.Handled = true;
        }
    }

    private CustomPopupPlacement[] PlacePalette(Size popupSize, Size targetSize, Point offset)
    {
        // The callback sizes can be in device pixels; scale our DIP offset the same way.
        var scale = ActualWidth > 0 ? targetSize.Width / ActualWidth : 1;
        var x = Math.Max(0, (targetSize.Width - popupSize.Width) / 2);
        return [new CustomPopupPlacement(new Point(x, PaletteTop * scale), PopupPrimaryAxis.None)];
    }
}
