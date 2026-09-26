using CommunityToolkit.Mvvm.Input;

namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>
/// A page or workspace section that reloads its data on demand. The shell runs
/// <see cref="RefreshCommand"/> when the user presses F5 (on the current section first, then on
/// the current page). A <c>[RelayCommand]</c> on <c>Task RefreshAsync()</c> implements it.
/// </summary>
public interface IRefreshable
{
    IAsyncRelayCommand RefreshCommand { get; }
}
