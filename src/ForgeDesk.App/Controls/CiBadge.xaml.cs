using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ForgeDesk.Core.GitHub;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Controls;

/// <summary>
/// GitHub Actions status for a branch: a colored icon plus a short label ("Passing", "Failing").
/// Set <see cref="ShowLabel"/> to false for icon-only use in dense rows (the tooltip explains it).
/// </summary>
public partial class CiBadge : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(CiState), typeof(CiBadge), new PropertyMetadata(CiState.Unknown, OnAppearanceChanged));

    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
        nameof(ShowLabel), typeof(bool), typeof(CiBadge), new PropertyMetadata(true, OnAppearanceChanged));

    public CiBadge()
    {
        InitializeComponent();
        ApplyState();
    }

    public CiState State
    {
        get => (CiState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CiBadge)d).ApplyState();

    private void ApplyState()
    {
        var state = State;
        StateIcon.Symbol = state switch
        {
            CiState.Success => SymbolRegular.CheckmarkCircle16,
            CiState.Failure => SymbolRegular.DismissCircle16,
            CiState.Running => SymbolRegular.ArrowSync16,
            CiState.Queued => SymbolRegular.Clock16,
            CiState.Cancelled => SymbolRegular.Prohibited16,
            CiState.None => SymbolRegular.SubtractCircle16,
            _ => SymbolRegular.QuestionCircle16,
        };
        StateIcon.SetResourceReference(IconElement.ForegroundProperty, StatusResources.BrushKey(StatusKinds.From(state)));
        StateLabel.Text = CiText.Label(state);
        StateLabel.Visibility = ShowLabel ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = CiText.Description(state);
        AutomationProperties.SetName(this, "CI: " + CiText.Label(state));
    }
}
