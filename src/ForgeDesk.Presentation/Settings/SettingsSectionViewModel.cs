using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Settings;

/// <summary>
/// A page of Settings (General, Appearance, Git…): its entry in the section list and its content.
/// Loads what it needs the first time it is shown and follows setting changes made elsewhere.
/// </summary>
public abstract class SettingsSectionViewModel : ViewModelBase
{
    private Task? _firstActivation;

    protected SettingsSectionViewModel(string key, string title, string icon, string description, string keywords)
    {
        Key = key;
        Title = title;
        Icon = icon;
        Description = description;
        Keywords = keywords;
    }

    /// <summary>Navigation name ("GitHub", "Data"); matched case-insensitively.</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon { get; }

    /// <summary>One line under the title.</summary>
    public string Description { get; }

    /// <summary>Extra words for the command palette.</summary>
    public string Keywords { get; }

    public string AutomationId => $"Settings.Section.{Key}";

    /// <summary>True while applying settings changed elsewhere: property handlers must not save them back.</summary>
    protected bool IsApplyingSettings { get; private set; }

    /// <summary>Called each time the section is shown.</summary>
    public Task ActivateAsync()
    {
        if (_firstActivation is null)
        {
            _firstActivation = LoadAsync();
            return _firstActivation;
        }

        return _firstActivation.IsCompleted ? OnReactivatedAsync() : _firstActivation;
    }

    /// <summary>First display: load data (git detection, shells, backups…).</summary>
    protected virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Later displays: refresh what may have changed meanwhile.</summary>
    protected virtual Task OnReactivatedAsync() => Task.CompletedTask;

    /// <summary>Follows settings changed elsewhere (palette theme toggle, clone folder remembered…).</summary>
    internal void ApplySettings(AppSettings settings)
    {
        IsApplyingSettings = true;
        try
        {
            OnSettingsChanged(settings);
        }
        finally
        {
            IsApplyingSettings = false;
        }
    }

    protected virtual void OnSettingsChanged(AppSettings settings)
    {
    }

    public override string ToString() => Title;
}

/// <summary>Validation of the number boxes of Settings.</summary>
internal static class SettingsNumbers
{
    /// <summary>Null when <paramref name="value"/> is a whole number within [min, max], else the message to show.</summary>
    public static string? ValidateWhole(double? value, int min, int max, string unit)
    {
        if (value is not { } number || double.IsNaN(number) || double.IsInfinity(number))
        {
            return $"Enter a number of {unit} between {min} and {max}.";
        }

        if (number < min || number > max)
        {
            return $"Use a value between {min} and {max} {unit}.";
        }

        return Math.Abs(number - Math.Round(number)) > 0.0001 ? "Use a whole number." : null;
    }

    /// <summary>Null when <paramref name="value"/> is within [min, max] (half steps allowed), else the message.</summary>
    public static string? ValidateSize(double? value, double min, double max)
    {
        if (value is not { } number || double.IsNaN(number) || double.IsInfinity(number))
        {
            return $"Enter a size between {min:0} and {max:0}.";
        }

        return number < min || number > max ? $"Use a size between {min:0} and {max:0}." : null;
    }
}
