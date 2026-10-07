using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Terminal;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Settings;

/// <summary>A shell of the default-shell drop-down; <see cref="Id"/> null means "the best one available".</summary>
public sealed record ShellOption(string? Id, string Name, string Icon, string? Detail)
{
    public static ShellOption Automatic(string? bestName) =>
        new(null, "Automatic", "Sparkle20", bestName is null ? "The best shell installed" : $"Currently {bestName}");

    public static ShellOption From(ShellProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ShellOption(profile.Id, profile.Name, IconFor(profile.Kind), profile.Executable);
    }

    /// <summary>WPF-UI symbol per kind of shell.</summary>
    public static string IconFor(ShellKind kind) => kind switch
    {
        ShellKind.PowerShellCore or ShellKind.WindowsPowerShell => "WindowConsole20",
        ShellKind.CommandPrompt => "AppGeneric20",
        ShellKind.GitBash => "BranchFork20",
        ShellKind.Wsl or ShellKind.Unix => "Code20",
        _ => "WindowDevTools20",
    };
}

/// <summary>Settings › Terminal: default shell and font size of the integrated terminal.</summary>
public sealed partial class TerminalSettingsViewModel : SettingsSectionViewModel
{
    private readonly SettingsStore _store;
    private readonly IShellDiscovery _discovery;
    private bool _loadingShells;

    internal TerminalSettingsViewModel(SettingsStore store, IShellDiscovery discovery)
        : base("Terminal", "Terminal", "WindowConsole20", "Default shell and font size", "terminal shell powershell cmd bash wsl console font")
    {
        _store = store;
        _discovery = discovery;
        ApplySettings(store.Current);
    }

    public ObservableCollection<ShellOption> Shells { get; } = [];

    [ObservableProperty]
    public partial ShellOption? SelectedShell { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingShells { get; private set; }

    /// <summary>Why the configured shell is not used (uninstalled…), or null.</summary>
    [ObservableProperty]
    public partial string? ShellWarning { get; private set; }

    [ObservableProperty]
    public partial double? TerminalFontSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTerminalFontSizeError))]
    public partial string? TerminalFontSizeError { get; private set; }

    public bool HasTerminalFontSizeError => TerminalFontSizeError is not null;

    protected override Task LoadAsync() => LoadShellsAsync();

    [RelayCommand]
    private async Task LoadShellsAsync()
    {
        IsLoadingShells = true;
        Error = null;
        try
        {
            var profiles = await _discovery.DiscoverAsync().ConfigureAwait(true);
            _loadingShells = true;
            Shells.Clear();
            Shells.Add(ShellOption.Automatic(profiles.FirstOrDefault()?.Name));
            foreach (var profile in profiles)
            {
                Shells.Add(ShellOption.From(profile));
            }

            SelectConfiguredShell(_store.Current.DefaultShellId);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Error = ErrorInfo.From(ex, "Could not list the installed shells");
        }
        finally
        {
            _loadingShells = false;
            IsLoadingShells = false;
        }
    }

    private void SelectConfiguredShell(string? id)
    {
        var configured = id is null ? null : Shells.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        ShellWarning = id is not null && configured is null && Shells.Count > 0
            ? "The shell chosen before is no longer installed. ForgeDesk uses the best one available until you choose another."
            : null;
        SelectedShell = configured ?? Shells.FirstOrDefault();
    }

    partial void OnSelectedShellChanged(ShellOption? value)
    {
        if (IsApplyingSettings || _loadingShells || value is null)
        {
            return;
        }

        ShellWarning = null;
        _ = _store.SaveAsync(s => s with { DefaultShellId = value.Id });
    }

    partial void OnTerminalFontSizeChanged(double? value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        TerminalFontSizeError = SettingsNumbers.ValidateSize(value, AppearanceSettingsViewModel.MinFontSize, AppearanceSettingsViewModel.MaxFontSize);
        if (TerminalFontSizeError is null && value is { } size)
        {
            _ = _store.SaveAsync(s => s with { TerminalFontSize = size });
        }
    }

    protected override void OnSettingsChanged(AppSettings settings)
    {
        TerminalFontSize = settings.TerminalFontSize;
        TerminalFontSizeError = null;
        if (Shells.Count > 0 && !string.Equals(SelectedShell?.Id, settings.DefaultShellId, StringComparison.OrdinalIgnoreCase))
        {
            SelectConfiguredShell(settings.DefaultShellId);
        }
    }
}
