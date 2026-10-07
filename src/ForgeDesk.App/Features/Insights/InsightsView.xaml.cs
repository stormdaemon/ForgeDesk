using System.Windows;
using System.Windows.Controls;

namespace ForgeDesk.App.Features.Insights;

/// <summary>
/// The Insights tab. Code-behind only switches the card grid between two columns and one column
/// (below <see cref="SingleColumnWidth"/>).
/// </summary>
public partial class InsightsView : UserControl
{
    public const double SingleColumnWidth = 1100;

    private bool? _singleColumn;

    public InsightsView()
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
        GapColumnDefinition.Width = new GridLength(single ? 0 : 12);
        RightColumnDefinition.Width = single ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RightColumn, single ? 0 : 2);
        Grid.SetRow(RightColumn, single ? 1 : 0);
    }
}
