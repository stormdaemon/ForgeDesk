using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Presentation.Settings;

/// <summary>A theme radio card (System, Dark, Light).</summary>
public sealed record ThemeOption(ThemePreference Value, string Title, string Description, string AutomationId)
{
    public bool IsSystem => Value == ThemePreference.System;

    public bool IsDark => Value == ThemePreference.Dark;

    public bool IsLight => Value == ThemePreference.Light;
}

/// <summary>Settings › Appearance: theme, accent, code and terminal fonts.</summary>
public sealed partial class AppearanceSettingsViewModel : SettingsSectionViewModel
{
    public const double MinFontSize = 8;
    public const double MaxFontSize = 32;
    public const string DefaultCodeFont = "Cascadia Code, Cascadia Mono, Consolas";

    private const string CodeFontKey = "appearance.codeFont";

    private readonly SettingsStore _store;

    internal AppearanceSettingsViewModel(SettingsStore store)
        : base("Appearance", "Appearance", "PaintBrush20", "Theme, accent color and fonts", "theme dark light system accent color font size code terminal")
    {
        _store = store;
        ThemeOptions =
        [
            new ThemeOption(ThemePreference.System, "System", "Follows the Windows setting", "Settings.ThemeSystem"),
            new ThemeOption(ThemePreference.Dark, "Dark", "Steel surfaces, easy on the eyes", "Settings.ThemeDark"),
            new ThemeOption(ThemePreference.Light, "Light", "Bright surfaces for daylight", "Settings.ThemeLight"),
        ];
        SelectedTheme = ThemeOptions[0];
        ApplySettings(store.Current);
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    [ObservableProperty]
    public partial ThemeOption SelectedTheme { get; set; }

    [ObservableProperty]
    public partial bool UseWindowsAccent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeFontError))]
    public partial string CodeFontFamily { get; set; } = DefaultCodeFont;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeFontError))]
    public partial string? CodeFontError { get; private set; }

    public bool HasCodeFontError => CodeFontError is not null;

    [ObservableProperty]
    public partial double? CodeFontSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeFontSizeError))]
    public partial string? CodeFontSizeError { get; private set; }

    public bool HasCodeFontSizeError => CodeFontSizeError is not null;

    [ObservableProperty]
    public partial double? TerminalFontSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTerminalFontSizeError))]
    public partial string? TerminalFontSizeError { get; private set; }

    public bool HasTerminalFontSizeError => TerminalFontSizeError is not null;

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (!IsApplyingSettings && value is not null)
        {
            _ = _store.SaveAsync(s => s with { Theme = value.Value });
        }
    }

    partial void OnUseWindowsAccentChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { UseSystemAccent = value });
        }
    }

    partial void OnCodeFontFamilyChanged(string value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        var font = value?.Trim() ?? string.Empty;
        CodeFontError = font.Length == 0 ? "Enter at least one font name, for example Cascadia Code." : null;
        if (CodeFontError is null)
        {
            _store.SaveLater(CodeFontKey, s => s with { CodeFontFamily = font });
        }
    }

    partial void OnCodeFontSizeChanged(double? value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        CodeFontSizeError = SettingsNumbers.ValidateSize(value, MinFontSize, MaxFontSize);
        if (CodeFontSizeError is null && value is { } size)
        {
            _ = _store.SaveAsync(s => s with { CodeFontSize = size });
        }
    }

    partial void OnTerminalFontSizeChanged(double? value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        TerminalFontSizeError = SettingsNumbers.ValidateSize(value, MinFontSize, MaxFontSize);
        if (TerminalFontSizeError is null && value is { } size)
        {
            _ = _store.SaveAsync(s => s with { TerminalFontSize = size });
        }
    }

    protected override void OnSettingsChanged(AppSettings settings)
    {
        SelectedTheme = ThemeOptions.FirstOrDefault(o => o.Value == settings.Theme) ?? ThemeOptions[0];
        UseWindowsAccent = settings.UseSystemAccent;
        if (!_store.IsPending(CodeFontKey))
        {
            CodeFontFamily = settings.CodeFontFamily;
            CodeFontError = null;
        }

        CodeFontSize = settings.CodeFontSize;
        CodeFontSizeError = null;
        TerminalFontSize = settings.TerminalFontSize;
        TerminalFontSizeError = null;
    }
}
