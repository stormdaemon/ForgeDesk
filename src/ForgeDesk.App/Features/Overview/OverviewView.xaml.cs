using System.Windows;
using System.Windows.Controls;

namespace ForgeDesk.App.Features.Overview;

/// <summary>
/// The Overview tab. Code-behind only switches the card grid between two columns and one column
/// (below <see cref="SingleColumnWidth"/>), which a Grid cannot express on its own.
/// </summary>
public partial class OverviewView : UserControl
{
    /// <summary>Below this width the cards stack in a single column.</summary>
    public const double SingleColumnWidth = 1100;

    private bool? _singleColumn;

    public OverviewView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var single = e.NewSize.Width < SingleColumnWidth;
        if (_singleColumn == single)
        {
            return;
        }

        _singleColumn = single;
        if (single)
        {
            GapColumnDefinition.Width = new GridLength(0);
            RightColumnDefinition.Width = new GridLength(0);
            Grid.SetColumn(RightColumn, 0);
            Grid.SetRow(RightColumn, 1);
        }
        else
        {
            GapColumnDefinition.Width = new GridLength(12);
            RightColumnDefinition.Width = new GridLength(5, GridUnitType.Star);
            Grid.SetColumn(RightColumn, 2);
            Grid.SetRow(RightColumn, 0);
        }
    }
}
