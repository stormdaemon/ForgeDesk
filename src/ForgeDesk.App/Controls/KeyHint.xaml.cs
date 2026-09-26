using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ForgeDesk.App.Controls;

/// <summary>Renders a keyboard shortcut as keycaps: <c>&lt;controls:KeyHint Keys="Ctrl+K" /&gt;</c>.</summary>
public partial class KeyHint : UserControl
{
    public static readonly DependencyProperty KeysProperty = DependencyProperty.Register(
        nameof(Keys), typeof(string), typeof(KeyHint), new PropertyMetadata(null, OnKeysChanged));

    public KeyHint()
    {
        InitializeComponent();
    }

    /// <summary>The shortcut, keys separated by '+' ("Ctrl+Shift+P", "Ctrl++", "Alt+Left").</summary>
    public string? Keys
    {
        get => (string?)GetValue(KeysProperty);
        set => SetValue(KeysProperty, value);
    }

    private static void OnKeysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var hint = (KeyHint)d;
        hint.Caps.ItemsSource = KeyHintParser.Parse(hint.Keys);
        AutomationProperties.SetName(hint, hint.Keys ?? string.Empty);
    }
}
