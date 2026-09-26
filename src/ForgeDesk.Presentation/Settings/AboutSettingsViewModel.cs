using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Settings;

/// <summary>An open-source component ForgeDesk is built with.</summary>
public sealed record ThirdPartyNotice(string Name, string License, string Purpose, string Url);

/// <summary>Settings › About: version, license, links and third-party notices.</summary>
public sealed partial class AboutSettingsViewModel : SettingsSectionViewModel
{
    public const string RepositoryUrl = "https://github.com/stormdaemon/ForgeDesk";
    public const string IssuesUrl = RepositoryUrl + "/issues/new";
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    private readonly IShellIntegration _shell;
    private readonly INotificationService _notifications;

    internal AboutSettingsViewModel(IUpdateService updates, IShellIntegration shell, INotificationService notifications)
        : base("About", "About", "Info20", "Version, license and credits", "about version license credits open source third party notices repository issue")
    {
        _shell = shell;
        _notifications = notifications;
        Version = updates.CurrentVersion;
    }

    public string Version { get; }

    public string VersionText => $"Version {Version}";

    public string Runtime => RuntimeInformation.FrameworkDescription;

    public string OperatingSystem => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public string License => "MIT License";

    public string Copyright => "© 2026 ForgeDesk contributors";

    public IReadOnlyList<ThirdPartyNotice> Notices { get; } =
    [
        new("WPF-UI", "MIT", "Fluent controls and theming", "https://github.com/lepoco/wpfui"),
        new("AvalonEdit", "MIT", "Code and diff viewer", "https://github.com/icsharpcode/AvalonEdit"),
        new("Octokit", "MIT", "GitHub API client", "https://github.com/octokit/octokit.net"),
        new("Velopack", "MIT", "Installer and updates", "https://github.com/velopack/velopack"),
        new("Microsoft Terminal control", "MIT", "Integrated terminal", "https://github.com/microsoft/terminal"),
        new("CommunityToolkit.Mvvm", "MIT", "MVVM source generators", "https://github.com/CommunityToolkit/dotnet"),
        new("Serilog", "Apache-2.0", "Application logs", "https://github.com/serilog/serilog"),
        new("Dapper", "Apache-2.0", "Database access", "https://github.com/DapperLib/Dapper"),
        new("Microsoft.Data.Sqlite", "MIT", "Local database", "https://github.com/dotnet/efcore"),
        new("Markdig", "BSD-2-Clause", "Markdown rendering", "https://github.com/xoofx/markdig"),
    ];

    [RelayCommand]
    private void OpenRepository() => Open(RepositoryUrl);

    [RelayCommand]
    private void ReportIssue() => Open(IssuesUrl);

    [RelayCommand]
    private void OpenLicense() => Open(LicenseUrl);

    [RelayCommand]
    private void OpenReleases() => Open(ReleasesUrl);

    [RelayCommand]
    private void OpenNotice(ThirdPartyNotice? notice)
    {
        if (notice is not null)
        {
            Open(notice.Url);
        }
    }

    /// <summary>Copies the version, runtime and OS, for bug reports.</summary>
    [RelayCommand]
    private void CopyVersionInfo()
    {
        try
        {
            _shell.CopyToClipboard($"ForgeDesk {Version}\n{Runtime}\n{OperatingSystem}");
            _notifications.Show("Version information copied", null, NotificationSeverity.Success);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    private void Open(string url)
    {
        try
        {
            _shell.OpenUrl(url);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }
}
