using ForgeDesk.App.Theming;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Services;

/// <summary>"Switch to light/dark theme": saves the opposite of what is on screen; ThemeService applies it.</summary>
internal sealed class ThemeSwitcher : IThemeSwitcher
{
    private readonly IThemeService _theme;
    private readonly ISettingsService _settings;

    public ThemeSwitcher(IThemeService theme, ISettingsService settings)
    {
        _theme = theme;
        _settings = settings;
    }

    public bool IsDark => _theme.IsDark;

    public Task ToggleAsync()
    {
        var next = _theme.IsDark ? ThemePreference.Light : ThemePreference.Dark;
        return _settings.UpdateAsync(s => s with { Theme = next });
    }
}
