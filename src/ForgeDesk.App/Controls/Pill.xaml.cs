using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Controls;

/// <summary>
/// A compact label on a tinted background ("main", "Draft", "3 behind"), colored by
/// <see cref="Kind"/> with the same palette as <see cref="StatusDot"/>. <see cref="Icon"/> is optional.
/// </summary>
public partial class Pill : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Pill), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(Pill), new PropertyMetadata(StatusKind.Neutral, OnAppearanceChanged));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(Pill), new PropertyMetadata(SymbolRegular.Empty, OnAppearanceChanged));

    public Pill()
    {
        InitializeComponent();
        ApplyAppearance();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Pill)d).ApplyAppearance();

    private void ApplyAppearance()
    {
        Surface.SetResourceReference(Border.BackgroundProperty, StatusResources.SubtleBrushKey(Kind));
        var textKey = StatusResources.TextBrushKey(Kind);
        PillText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, textKey);
        PillIcon.SetResourceReference(IconElement.ForegroundProperty, textKey);
        PillIcon.Symbol = Icon;
        PillIcon.Visibility = Icon == SymbolRegular.Empty ? Visibility.Collapsed : Visibility.Visible;
    }
}
