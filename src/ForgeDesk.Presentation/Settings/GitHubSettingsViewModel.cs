using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Settings;

/// <summary>Settings › GitHub: the account (or the ways to sign in), GitHub features and the API rate limit.</summary>
public sealed partial class GitHubSettingsViewModel : SettingsSectionViewModel, IDisposable
{
    private readonly SettingsStore _store;
    private readonly IGitHubAccountService _accounts;
    private readonly IGitHubService _github;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger _logger;

    internal GitHubSettingsViewModel(
        SettingsStore store,
        IGitHubAccountService accounts,
        IGitHubService github,
        IShellIntegration shell,
        IDialogService dialogs,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger logger)
        : base("GitHub", "GitHub", "Globe20", "Account, sign-in and GitHub features", "github account sign in sign out token login rate limit api cli credential manager")
    {
        _store = store;
        _accounts = accounts;
        _github = github;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        SignIn = new GitHubSignInViewModel(accounts, shell, dispatcher, logger);
        SignIn.PropertyChanged += OnSignInPropertyChanged;
        ApplySettings(store.Current);
    }

    /// <summary>The account card and the sign-in methods.</summary>
    public GitHubSignInViewModel SignIn { get; }

    [ObservableProperty]
    public partial bool GitHubEnabled { get; set; }

    [ObservableProperty]
    public partial RateLimitInfo? RateLimit { get; private set; }

    /// <summary>"4,832 of 5,000 requests left · resets at 14:05".</summary>
    [ObservableProperty]
    public partial string? RateLimitText { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingRateLimit { get; private set; }

    /// <summary>0..1 of the hourly budget still available.</summary>
    [ObservableProperty]
    public partial double RateLimitRemainingFraction { get; private set; }

    [ObservableProperty]
    public partial bool IsSigningOut { get; private set; }

    protected override async Task LoadAsync()
    {
        await SignIn.InitializeAsync().ConfigureAwait(true);
        await RefreshRateLimitAsync().ConfigureAwait(true);
    }

    protected override Task OnReactivatedAsync() => RefreshRateLimitAsync();

    [RelayCommand]
    private async Task RefreshRateLimitAsync()
    {
        if (!SignIn.IsSignedIn)
        {
            RateLimit = null;
            RateLimitText = null;
            return;
        }

        IsLoadingRateLimit = true;
        try
        {
            var limit = await _github.GetRateLimitAsync().ConfigureAwait(true);
            RateLimit = limit;
            if (limit is null)
            {
                RateLimitText = "GitHub didn't report a rate limit.";
                RateLimitRemainingFraction = 0;
            }
            else
            {
                var culture = CultureInfo.CurrentCulture;
                RateLimitText = $"{limit.Remaining.ToString("N0", culture)} of {limit.Limit.ToString("N0", culture)} requests left this hour · resets at {limit.ResetsAt.ToLocalTime().ToString("t", culture)}";
                RateLimitRemainingFraction = limit.Limit <= 0 ? 0 : Math.Clamp((double)limit.Remaining / limit.Limit, 0, 1);
            }
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogDebug(ex, "Could not read the GitHub rate limit");
            RateLimit = null;
            RateLimitText = $"Couldn't read the rate limit: {ErrorInfo.From(ex).Message}";
        }
        finally
        {
            IsLoadingRateLimit = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (SignIn.Login is not { } login)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = $"Sign out of {login}?",
            Message = "ForgeDesk forgets its GitHub token. Issues, pull requests, CI status and releases stop loading until you sign in again. "
                + "Your repositories and local projects are not affected.",
            ConfirmText = "Sign out",
            IsDestructive = true,
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        IsSigningOut = true;
        try
        {
            await _accounts.SignOutAsync().ConfigureAwait(true);
            _notifications.Show("Signed out of GitHub", $"ForgeDesk no longer uses the account {login}.", NotificationSeverity.Success);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not sign out"));
        }
        finally
        {
            IsSigningOut = false;
        }
    }

    partial void OnGitHubEnabledChanged(bool value)
    {
        if (!IsApplyingSettings)
        {
            _ = _store.SaveAsync(s => s with { GitHubEnabled = value });
        }
    }

    protected override void OnSettingsChanged(AppSettings settings) => GitHubEnabled = settings.GitHubEnabled;

    private void OnSignInPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitHubSignInViewModel.Account))
        {
            _ = RefreshRateLimitAsync();
        }
    }

    public void Dispose()
    {
        SignIn.PropertyChanged -= OnSignInPropertyChanged;
        SignIn.Dispose();
    }
}
