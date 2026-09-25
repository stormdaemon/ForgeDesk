using System.Windows;

namespace ForgeDesk.App.Startup;

/// <summary>Small startup card, shown only when startup takes longer than half a second.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }
}
