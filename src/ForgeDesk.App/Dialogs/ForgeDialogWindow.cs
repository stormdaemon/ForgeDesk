using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Dialogs;

/// <summary>
/// Base for ForgeDesk's modal windows: Fluent chrome (Mica, rounded corners), no taskbar entry,
/// sized to content, centered on its owner, and closed by Esc.
/// </summary>
public class ForgeDialogWindow : FluentWindow
{
    public ForgeDialogWindow()
    {
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;

        // The FluentWindow style imposes a 1100×600 window with a 320 px minimum height.
        Height = double.NaN;
        MinHeight = 0;
        MinWidth = 320;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(FontFamilyProperty, "ForgeUIFont");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Shows the window modally over <paramref name="owner"/> (centered on screen without one).</summary>
    public void ShowModal(Window? owner)
    {
        if (owner is { IsLoaded: true, IsVisible: true } && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        ShowDialog();
    }
}
