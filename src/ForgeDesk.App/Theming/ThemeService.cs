using System.Windows;
using System.Windows.Media;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Theming;

/// <summary>Current light/dark state of the UI, for views that render their own colors.</summary>
public interface IThemeService
{
    /// <summary>True when the UI uses dark surfaces (Dark theme, or a dark high-contrast theme).</summary>
    bool IsDark { get; }

    /// <summary>Raised on the UI thread after the theme, the accent or the brand palette changed.</summary>
    event EventHandler? ThemeChanged;
}

/// <summary>
/// Applies the Theme/UseSystemAccent/code font preferences: WPF-UI theme dictionaries, the
/// ember (or Windows) accent, and the Forge brand palette. Follows the Windows theme through
/// <see cref="SystemThemeWatcher"/> when the preference is System, and re-applies whenever the
/// settings change.
/// </summary>
internal sealed class ThemeService : IThemeService
{
    private const string DefaultCodeFont = "Cascadia Code, Cascadia Mono, Consolas";

    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ThemeService> _logger;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private Window? _window;
    private bool _watching;
    private bool _watchingAccents;
    private bool _applyingTheme;
    private AppSettings? _applied;

    public ThemeService(ISettingsService settings, IUiDispatcher dispatcher, ILogger<ThemeService> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public bool IsDark { get; private set; } = true;

    public event EventHandler? ThemeChanged;

    /// <summary>Applies the saved preferences. Call on the UI thread before the first window is shown.</summary>
    public void Initialize()
    {
        _settings.Changed += OnSettingsChanged;
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        Apply(_settings.Current);
    }

    /// <summary>Lets the service follow the Windows theme through the main window's messages.</summary>
    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        if (window.IsLoaded)
        {
            UpdateWatcher(_settings.Current);
        }
        else
        {
            window.Loaded += OnWindowLoaded;
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ((Window)sender).Loaded -= OnWindowLoaded;
        UpdateWatcher(_settings.Current);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.Post(() =>
    {
        var previous = _applied;
        if (previous is null
            || previous.Theme != settings.Theme
            || previous.UseSystemAccent != settings.UseSystemAccent
            || !string.Equals(previous.CodeFontFamily, settings.CodeFontFamily, StringComparison.Ordinal)
            || !previous.CodeFontSize.Equals(settings.CodeFontSize))
        {
            Apply(settings);
        }
    });

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        // Raised by WPF-UI inside ApplicationThemeManager.Apply — including when the system theme
        // watcher follows Windows. Our own Apply() finishes the job itself.
        if (!_applyingTheme)
        {
            ApplyAccentAndPalette(_settings.Current.UseSystemAccent);
        }
    }

    private void Apply(AppSettings settings)
    {
        try
        {
            _applyingTheme = true;
            if (settings.Theme == ThemePreference.System)
            {
                ApplicationThemeManager.ApplySystemTheme(updateAccent: false);
            }
            else
            {
                var theme = settings.Theme == ThemePreference.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
                ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
            }
        }
        finally
        {
            _applyingTheme = false;
        }

        // Fonts first: the palette step raises the change notification views react to.
        ApplyCodeFont(settings);
        ApplyAccentAndPalette(settings.UseSystemAccent);
        UpdateWatcher(settings);
        _applied = settings;
        _logger.LogInformation("Applied {Theme} theme ({Accent} accent)", IsDark ? "dark" : "light",
            settings.UseSystemAccent ? "Windows" : "ember");
    }

    private void ApplyAccentAndPalette(bool useSystemAccent)
    {
        var highContrast = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.HighContrast;
        var dark = ThemeProbe.IsDark;

        IReadOnlyDictionary<string, Argb> palette;
        if (useSystemAccent || highContrast)
        {
            // High-contrast themes keep the colors the user chose in Windows.
            ApplicationAccentColorManager.ApplySystemAccent();
            palette = BrandPalette.Build(dark,
                ToArgb(ApplicationAccentColorManager.SystemAccent),
                ToArgb(ApplicationAccentColorManager.SecondaryAccent));
        }
        else
        {
            var (system, primary, secondary, tertiary) = BrandPalette.EmberAccent(dark);
            ApplicationAccentColorManager.Apply(ToColor(system), ToColor(primary), ToColor(secondary), ToColor(tertiary));
            palette = BrandPalette.ForEmber(dark);
        }

        ApplyPalette(palette);
        IsDark = dark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        ThemeProbe.RaiseChanged();
    }

    private void ApplyPalette(IReadOnlyDictionary<string, Argb> palette)
    {
        var resources = Application.Current.Resources;
        foreach (var (name, value) in palette)
        {
            var color = ToColor(value);
            resources[name + "Color"] = color;

            // Brushes are created once and then recolored in place, so that brushes already
            // handed out (by converters, or captured in bindings) follow theme changes too.
            if (_brushes.TryGetValue(name, out var brush))
            {
                brush.Color = color;
            }
            else
            {
                brush = new SolidColorBrush(color);
                _brushes[name] = brush;
                resources[name + "Brush"] = brush;
            }
        }
    }

    private static void ApplyCodeFont(AppSettings settings)
    {
        var resources = Application.Current.Resources;
        var family = string.IsNullOrWhiteSpace(settings.CodeFontFamily) ? DefaultCodeFont : settings.CodeFontFamily;
        resources["ForgeCodeFont"] = new FontFamily(family);
        resources["ForgeCodeFontSize"] = Math.Clamp(double.IsFinite(settings.CodeFontSize) ? settings.CodeFontSize : 13, 8, 32);
    }

    private void UpdateWatcher(AppSettings settings)
    {
        if (_window is not { IsLoaded: true } window)
        {
            return;
        }

        var shouldWatch = settings.Theme == ThemePreference.System;
        if (shouldWatch == _watching && (!shouldWatch || _watchingAccents == settings.UseSystemAccent))
        {
            return;
        }

        if (_watching)
        {
            SystemThemeWatcher.UnWatch(window);
            _watching = false;
        }

        if (shouldWatch)
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: settings.UseSystemAccent);
            _watching = true;
            _watchingAccents = settings.UseSystemAccent;
        }
    }

    private static Argb ToArgb(Color color) => new(color.A, color.R, color.G, color.B);

    private static Color ToColor(Argb color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
