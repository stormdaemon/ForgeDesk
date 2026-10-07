using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Settings;

/// <summary>A pull strategy choice with its one-line explanation.</summary>
public sealed record PullStrategyOption(PullStrategy Value, string Title, string Description, string AutomationId);

/// <summary>Settings › Git: which git runs, the commit identity, pulling and fetching.</summary>
public sealed partial class GitSettingsViewModel : SettingsSectionViewModel
{
    public const string DownloadGitUrl = "https://git-scm.com/download/win";
    public const int MinFetchInterval = 1;
    public const int MaxFetchInterval = 120;

    /// <summary>Oldest git ForgeDesk supports (matches the git service's requirement).</summary>
    public static readonly Version MinimumGitVersion = new(2, 31);

    private const string IdentityKey = "git.identity";

    private readonly SettingsStore _store;
    private readonly IGitService _git;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IShellIntegration _shell;
    private readonly ILogger _logger;
    private GitIdentity _savedIdentity = new(null, null);

    internal GitSettingsViewModel(SettingsStore store, IGitService git, IDialogService dialogs, INotificationService notifications, IShellIntegration shell, ILogger logger)
        : base("Git", "Git", "BranchFork20", "Git program, identity, pulling and fetching", "git executable path version identity author name email pull rebase merge fetch credentials")
    {
        _store = store;
        _git = git;
        _dialogs = dialogs;
        _notifications = notifications;
        _shell = shell;
        _logger = logger;
        PullStrategies =
        [
            new PullStrategyOption(PullStrategy.FastForwardOnly, "Fast-forward only",
                "Never creates merge commits: stops and asks you when your branch and the remote have both moved.", "Settings.PullFastForward"),
            new PullStrategyOption(PullStrategy.Merge, "Merge",
                "Joins the remote commits with a merge commit when both sides have new work.", "Settings.PullMerge"),
            new PullStrategyOption(PullStrategy.Rebase, "Rebase",
                "Replays your local commits on top of the remote ones for a straight history.", "Settings.PullRebase"),
        ];
        SelectedPullStrategy = PullStrategies[0];
        ApplySettings(store.Current);
    }

    // ----- Git program ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitFound), nameof(GitPath), nameof(GitVersion), nameof(IsGitTooOld), nameof(GitStatusText), nameof(GitStatusTone))]
    public partial GitInstallation? Installation { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitStatusText), nameof(GitStatusTone))]
    public partial bool IsDetecting { get; private set; }

    /// <summary>The git chosen in Settings, or null to use the one installed on the computer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomGitPath))]
    [NotifyCanExecuteChangedFor(nameof(ResetGitPathCommand))]
    public partial string? CustomGitPath { get; private set; }

    public bool HasCustomGitPath => CustomGitPath is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitPathError))]
    public partial string? GitPathError { get; private set; }

    public bool HasGitPathError => GitPathError is not null;

    public bool IsGitFound => Installation is not null;

    public string? GitPath => Installation?.ExecutablePath;

    public string? GitVersion => Installation?.Version;

    public bool IsGitTooOld => Installation is { } installation
        && Version.TryParse(installation.Version, out var version) && version < MinimumGitVersion;

    public string GitStatusText => IsDetecting
        ? "Looking for Git…"
        : Installation switch
        {
            null when HasCustomGitPath => "The Git program set below can't be used.",
            null => "Git was not found on this computer.",
            _ when IsGitTooOld => $"Git {GitVersion} is too old: ForgeDesk needs {MinimumGitVersion} or later.",
            _ => $"Git {GitVersion}",
        };

    public StatusTone GitStatusTone => IsDetecting ? StatusTone.Running : Installation is null ? StatusTone.Danger : IsGitTooOld ? StatusTone.Warning : StatusTone.Success;

    // ----- Identity ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string AuthorName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AuthorEmail { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthorNameError))]
    public partial string? AuthorNameError { get; private set; }

    public bool HasAuthorNameError => AuthorNameError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthorEmailError))]
    public partial string? AuthorEmailError { get; private set; }

    public bool HasAuthorEmailError => AuthorEmailError is not null;

    [ObservableProperty]
    public partial bool IsIdentityLoaded { get; private set; }

    /// <summary>"Saved" feedback after the identity was written to the global git configuration.</summary>
    [ObservableProperty]
    public partial string? IdentityStatus { get; private set; }

    // ----- Pull and fetch ---------------------------------------------------------------------

    public IReadOnlyList<PullStrategyOption> PullStrategies { get; }

    [ObservableProperty]
    public partial PullStrategyOption SelectedPullStrategy { get; set; }

    [ObservableProperty]
    public partial bool AutoFetch { get; set; }

    [ObservableProperty]
    public partial double? AutoFetchInterval { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoFetchIntervalError))]
    public partial string? AutoFetchIntervalError { get; private set; }

    public bool HasAutoFetchIntervalError => AutoFetchIntervalError is not null;

    [ObservableProperty]
    public partial bool UseGitHubSignInForGit { get; set; }

    // ----- Loading ----------------------------------------------------------------------------

    protected override async Task LoadAsync()
    {
        await DetectAsync().ConfigureAwait(true);
        await LoadIdentityAsync().ConfigureAwait(true);
    }

    protected override Task OnReactivatedAsync() => IsGitFound ? Task.CompletedTask : DetectAsync();

    [RelayCommand]
    private async Task DetectAsync()
    {
        IsDetecting = true;
        try
        {
            Installation = await _git.FindGitAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Git detection failed");
            Installation = null;
        }
        finally
        {
            IsDetecting = false;
        }
    }

    private async Task LoadIdentityAsync()
    {
        if (!IsGitFound)
        {
            return;
        }

        try
        {
            // Outside any repository git reports the global (and system) identity.
            var identity = await _git.GetIdentityAsync(IdentityProbeDirectory()).ConfigureAwait(true);
            _savedIdentity = identity;
            IsApplyingIdentity = true;
            AuthorName = identity.Name ?? string.Empty;
            AuthorEmail = identity.Email ?? string.Empty;
            IsIdentityLoaded = true;
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not read the git identity");
            IdentityStatus = "Your Git identity could not be read.";
        }
        finally
        {
            IsApplyingIdentity = false;
        }
    }

    private bool IsApplyingIdentity { get; set; }

    internal static string IdentityProbeDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? Path.GetTempPath() : profile;
    }

    // ----- Git program actions ----------------------------------------------------------------

    [RelayCommand]
    private async Task BrowseGitAsync()
    {
        IReadOnlyList<string> files;
        try
        {
            files = await _dialogs.PickFilesAsync("Choose the Git program (git.exe)", GitPath is { } path ? Path.GetDirectoryName(path) : null, allowMultiple: false)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the file picker"));
            return;
        }

        if (files.Count > 0)
        {
            await UseGitPathAsync(files[0]).ConfigureAwait(true);
        }
    }

    /// <summary>Uses <paramref name="path"/> as git when it really is a working git; otherwise keeps the previous choice.</summary>
    internal async Task<bool> UseGitPathAsync(string path)
    {
        var candidate = path.Trim().Trim('"');
        if (candidate.Length == 0)
        {
            return false;
        }

        GitPathError = null;
        var previous = _store.Current.GitExecutablePath;
        if (!await _store.SaveAsync(s => s with { GitExecutablePath = candidate }).ConfigureAwait(true))
        {
            return false;
        }

        IsDetecting = true;
        GitInstallation? found;
        try
        {
            found = await _git.FindGitAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Probing {Path} failed", candidate);
            found = null;
        }
        finally
        {
            IsDetecting = false;
        }

        if (found is null)
        {
            await _store.SaveAsync(s => s with { GitExecutablePath = previous }).ConfigureAwait(true);
            CustomGitPath = string.IsNullOrWhiteSpace(previous) ? null : previous;
            await DetectAsync().ConfigureAwait(true);
            GitPathError = $"“{candidate}” isn't a working Git program. Choose git.exe in your Git installation, "
                + @"for example C:\Program Files\Git\cmd\git.exe.";
            return false;
        }

        CustomGitPath = candidate;
        Installation = found;
        _notifications.Show("ForgeDesk now uses this Git", $"Git {found.Version} — {found.ExecutablePath}", NotificationSeverity.Success);
        if (!IsIdentityLoaded)
        {
            await LoadIdentityAsync().ConfigureAwait(true);
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(HasCustomGitPath))]
    private async Task ResetGitPathAsync()
    {
        GitPathError = null;
        if (await _store.SaveAsync(s => s with { GitExecutablePath = null }).ConfigureAwait(true))
        {
            CustomGitPath = null;
            await DetectAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void DownloadGit()
    {
        try
        {
            _shell.OpenUrl(DownloadGitUrl);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }

    // ----- Identity actions -------------------------------------------------------------------

    partial void OnAuthorNameChanged(string value) => OnIdentityEdited();

    partial void OnAuthorEmailChanged(string value) => OnIdentityEdited();

    private void OnIdentityEdited()
    {
        if (IsApplyingIdentity || !IsIdentityLoaded)
        {
            return;
        }

        IdentityStatus = null;
        AuthorNameError = ValidateName(AuthorName);
        AuthorEmailError = ValidateEmail(AuthorEmail);
        if (AuthorNameError is null && AuthorEmailError is null)
        {
            _store.RunLater(IdentityKey, SaveIdentityAsync);
        }
    }

    private async Task SaveIdentityAsync()
    {
        var name = AuthorName.Trim();
        var email = AuthorEmail.Trim();
        if (ValidateName(name) is not null || ValidateEmail(email) is not null
            || (string.Equals(name, _savedIdentity.Name, StringComparison.Ordinal) && string.Equals(email, _savedIdentity.Email, StringComparison.Ordinal)))
        {
            return;
        }

        try
        {
            await _git.SetGlobalIdentityAsync(name, email).ConfigureAwait(true);
            _savedIdentity = new GitIdentity(name, email);
            IdentityStatus = "Saved to your global Git configuration.";
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Could not save the git identity");
            _notifications.ShowError(ErrorInfo.From(ex, "Could not save your Git identity"));
        }
    }

    internal static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Enter the name shown on your commits." : name.Any(char.IsControl) ? "The name can't contain control characters." : null;

    internal static string? ValidateEmail(string? email)
    {
        var trimmed = email?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "Enter the email address of your commits.";
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 || at == trimmed.Length - 1 || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? "Enter a valid email address, for example you@example.com."
            : null;
    }

    // ----- Pull and fetch actions -------------------------------------------------------------

    partial void OnSelectedPullStrategyChanged(PullStrategyOption value)
    {
        if (!IsApplyingSettings && value is not null)
        {
            _ = _store.SaveAsync(s => s with { PullStrategy = value.Value });
        }
    }

    partial void OnAutoFetchChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { AutoFetch = value });
        }
    }

    partial void OnAutoFetchIntervalChanged(double? value)
    {
        if (IsApplyingSettings)
        {
            return;
        }

        AutoFetchIntervalError = SettingsNumbers.ValidateWhole(value, MinFetchInterval, MaxFetchInterval, "minutes");
        if (AutoFetchIntervalError is null && value is { } minutes)
        {
            _ = _store.SaveAsync(s => s with { AutoFetchIntervalMinutes = (int)Math.Round(minutes) });
        }
    }

    partial void OnUseGitHubSignInForGitChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { UseGitHubTokenForGit = value });
        }
    }

    protected override void OnSettingsChanged(AppSettings settings)
    {
        CustomGitPath = string.IsNullOrWhiteSpace(settings.GitExecutablePath) ? null : settings.GitExecutablePath;
        SelectedPullStrategy = PullStrategies.FirstOrDefault(o => o.Value == settings.PullStrategy) ?? PullStrategies[0];
        AutoFetch = settings.AutoFetch;
        AutoFetchInterval = settings.AutoFetchIntervalMinutes;
        AutoFetchIntervalError = null;
        UseGitHubSignInForGit = settings.UseGitHubTokenForGit;
    }
}
