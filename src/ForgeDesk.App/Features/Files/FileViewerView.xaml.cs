using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ForgeDesk.App.Features.Files;

/// <summary>The file preview of the Files tab. View-only behavior: the header's overflow menu.</summary>
public partial class FileViewerView : UserControl
{
    public FileViewerView()
    {
        InitializeComponent();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Resources["MoreMenu"] is ContextMenu menu)
        {
            menu.PlacementTarget = MoreButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
