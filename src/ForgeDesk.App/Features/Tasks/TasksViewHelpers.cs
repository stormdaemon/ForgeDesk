using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ForgeDesk.Presentation.Tasks;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Features.Tasks;

/// <summary>
/// Focuses the element when <see cref="RequestProperty"/> changes (and the element is visible):
/// the view model increments a counter to ask for focus, even when the box is already open.
/// </summary>
public static class FocusRequest
{
    public static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached(
        "Request", typeof(int), typeof(FocusRequest), new PropertyMetadata(0, OnRequestChanged));

    public static int GetRequest(DependencyObject element) => (int)(element?.GetValue(RequestProperty) ?? 0);

    public static void SetRequest(DependencyObject element, int value) => element?.SetValue(RequestProperty, value);

    private static void OnRequestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || (int)e.NewValue == 0)
        {
            return;
        }

        // After the box became visible and was laid out.
        element.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (element.IsVisible)
            {
                element.Focus();
                Keyboard.Focus(element);
                if (element is System.Windows.Controls.TextBox box)
                {
                    box.CaretIndex = box.Text.Length;
                }
            }
        });
    }
}

/// <summary>Arrow of the list view's sort column: values = [SortColumn, SortDescending], parameter = the column.</summary>
public sealed class TaskSortGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length < 2 || values[0] is not TaskSortColumn current || values[1] is not bool descending
            || parameter is not string name || !Enum.TryParse<TaskSortColumn>(name, out var column) || column != current)
        {
            return SymbolRegular.Empty;
        }

        return descending ? SymbolRegular.ArrowSortDown16 : SymbolRegular.ArrowSortUp16;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>The insertion line shown while a card is dragged over a column (between two cards).</summary>
public sealed class DropIndicatorAdorner : Adorner
{
    private readonly Pen _pen;
    private readonly Brush _brush;
    private double _y = double.NaN;

    public DropIndicatorAdorner(UIElement adornedElement)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
        _brush = (adornedElement as FrameworkElement)?.TryFindResource("ForgeAccentBrush") as Brush ?? Brushes.DarkOrange;
        _pen = new Pen(_brush, 2);
    }

    /// <summary>Vertical position of the line, relative to the adorned element.</summary>
    public void Update(double y)
    {
        if (Math.Abs(_y - y) > 0.5)
        {
            _y = y;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (double.IsNaN(_y))
        {
            return;
        }

        var width = AdornedElement.RenderSize.Width;
        drawingContext.DrawLine(_pen, new Point(12, _y), new Point(width - 12, _y));
        drawingContext.DrawEllipse(_brush, null, new Point(10, _y), 3.5, 3.5);
    }
}
