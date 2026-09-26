namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>Switches between the light and dark themes (implemented by the app).</summary>
public interface IThemeSwitcher
{
    /// <summary>True when the UI currently renders dark surfaces.</summary>
    bool IsDark { get; }

    /// <summary>Saves the opposite of the current theme as the user's preference.</summary>
    Task ToggleAsync();
}
