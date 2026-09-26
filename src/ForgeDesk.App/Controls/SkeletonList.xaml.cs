using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ForgeDesk.App.Controls;

/// <summary>
/// Placeholder rows shown while a list loads (DESIGN.md "Loading" state): <see cref="RowCount"/>
/// dense rows of rounded bars with a subtle shimmer. Appears only after a short delay and stops
/// animating when hidden or when Windows animations are turned off.
/// </summary>
public partial class SkeletonList : UserControl
{
    public static readonly DependencyProperty RowCountProperty = DependencyProperty.Register(
        nameof(RowCount), typeof(int), typeof(SkeletonList), new PropertyMetadata(8, OnLayoutChanged), IsValidCount);

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(SkeletonList), new PropertyMetadata(32.0, OnLayoutChanged), IsValidHeight);

    public static readonly DependencyProperty ShowIconsProperty = DependencyProperty.Register(
        nameof(ShowIcons), typeof(bool), typeof(SkeletonList), new PropertyMetadata(true, OnLayoutChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SkeletonList), new PropertyMetadata(true, OnActiveChanged));

    // Varied but deterministic bar lengths, so the placeholder looks like real content.
    private static readonly double[] PrimaryWidths = [0.62, 0.48, 0.74, 0.55, 0.68, 0.42, 0.58, 0.8, 0.5, 0.66];

    private readonly DelayedReveal _reveal;

    public SkeletonList()
    {
        InitializeComponent();
        BuildRows();
        _reveal = new DelayedReveal(this, Body, () => IsActive, OnRevealChanged);
    }

    /// <summary>Number of placeholder rows (default 8).</summary>
    public int RowCount
    {
        get => (int)GetValue(RowCountProperty);
        set => SetValue(RowCountProperty, value);
    }

    /// <summary>Height of each row (32 for lists, 28 for tables).</summary>
    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    /// <summary>Show a round icon placeholder at the start of each row.</summary>
    public bool ShowIcons
    {
        get => (bool)GetValue(ShowIconsProperty);
        set => SetValue(ShowIconsProperty, value);
    }

    /// <summary>Bind to the busy flag when the control stays visible between loads.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static bool IsValidCount(object value) => value is >= 0 and <= 200;

    private static bool IsValidHeight(object value) => value is double height && height >= 8 && double.IsFinite(height);

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SkeletonList)d).BuildRows();

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SkeletonList)d)._reveal?.Update();

    private void BuildRows()
    {
        Rows.Children.Clear();
        for (var index = 0; index < RowCount; index++)
        {
            Rows.Children.Add(CreateRow(index));
        }
    }

    private Grid CreateRow(int index)
    {
        var primary = PrimaryWidths[index % PrimaryWidths.Length];
        var row = new Grid { Height = RowHeight, Margin = new Thickness(16, 0, 16, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(primary, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - primary, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (ShowIcons)
        {
            var icon = new Ellipse { Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0) };
            icon.SetResourceReference(Shape.FillProperty, "ForgeSkeletonBrush");
            row.Children.Add(icon);
        }

        row.Children.Add(Bar(column: 1, width: double.NaN));
        row.Children.Add(Bar(column: 3, width: index % 3 == 0 ? 56 : 40));
        return row;
    }

    private static Border Bar(int column, double width)
    {
        var bar = new Border { Height = 10, CornerRadius = new CornerRadius(4), Width = width, VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(Border.BackgroundProperty, "ForgeSkeletonBrush");
        Grid.SetColumn(bar, column);
        return bar;
    }

    private void OnRevealChanged(bool revealed)
    {
        if (revealed && SystemParameters.ClientAreaAnimation)
        {
            var sweep = new DoubleAnimation(-1, 1, new Duration(TimeSpan.FromSeconds(1.4)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            ShimmerOffset.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        else
        {
            ShimmerOffset.BeginAnimation(TranslateTransform.XProperty, null);
        }

        Shimmer.Visibility = revealed && SystemParameters.ClientAreaAnimation ? Visibility.Visible : Visibility.Collapsed;
    }
}
