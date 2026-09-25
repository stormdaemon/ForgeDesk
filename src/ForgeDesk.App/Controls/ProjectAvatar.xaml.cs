using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ForgeDesk.App.Theming;

namespace ForgeDesk.App.Controls;

/// <summary>
/// Rounded-square project avatar with the project's initials. <see cref="Color"/> is the
/// project's chosen #RRGGBB color; without one, a stable color is derived from the name.
/// </summary>
public partial class ProjectAvatar : UserControl
{
    public static readonly DependencyProperty ProjectNameProperty = DependencyProperty.Register(
        nameof(ProjectName), typeof(string), typeof(ProjectAvatar), new PropertyMetadata(null, OnAppearanceChanged));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ProjectAvatar), new PropertyMetadata(null, OnAppearanceChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ProjectAvatar), new PropertyMetadata(32.0, OnAppearanceChanged), IsValidSize);

    public ProjectAvatar()
    {
        InitializeComponent();
        Render();
    }

    public string? ProjectName
    {
        get => (string?)GetValue(ProjectNameProperty);
        set => SetValue(ProjectNameProperty, value);
    }

    /// <summary>Optional #RRGGBB color (Project.Color).</summary>
    public string? Color
    {
        get => (string?)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Edge length in pixels (default 32).</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static bool IsValidSize(object value) => value is double size && size > 0 && double.IsFinite(size);

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ProjectAvatar)d).Render();

    private void Render()
    {
        var size = Size;
        Tile.Width = size;
        Tile.Height = size;
        Tile.CornerRadius = new CornerRadius(Math.Max(4, Math.Round(size * 0.25)));
        InitialsText.FontSize = Math.Max(8, Math.Round(size * 0.4));
        InitialsText.Text = AvatarText.Initials(ProjectName);

        var background = ColorMath.TryParseHex(Color, out var chosen) && chosen.A == 255 ? chosen : AvatarText.ColorFor(ProjectName);
        Tile.Background = Frozen(background);
        InitialsText.Foreground = Frozen(ColorMath.ReadableTextOn(background));
        AutomationProperties.SetName(this, ProjectName ?? string.Empty);
    }

    private static SolidColorBrush Frozen(Argb color)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
