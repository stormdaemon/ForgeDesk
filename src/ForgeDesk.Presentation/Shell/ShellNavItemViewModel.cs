using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Shell;

/// <summary>A fixed sidebar entry (Home, Activity).</summary>
public sealed partial class ShellNavItemViewModel : ObservableObject
{
    public ShellNavItemViewModel(PageKind kind, string title, string icon, bool isAvailable, Action navigate, string? shortcut = null)
    {
        ArgumentNullException.ThrowIfNull(navigate);
        Kind = kind;
        Title = title;
        Icon = icon;
        IsAvailable = isAvailable;
        Shortcut = shortcut;
        NavigateCommand = new RelayCommand(navigate);
    }

    public PageKind Kind { get; }

    public string Title { get; }

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon { get; }

    public string? Shortcut { get; }

    public string ToolTip => Shortcut is null ? Title : $"{Title} ({Shortcut})";

    /// <summary>False when the page is not registered: the entry is hidden.</summary>
    public bool IsAvailable { get; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public IRelayCommand NavigateCommand { get; }
}
