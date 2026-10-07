using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Presentation.Settings;

public enum GitHubSignInMethod
{
    Browser,
    GitHubCli,
    Token,
}

/// <summary>
/// The GitHub account and the three ways to sign in (browser through Git Credential Manager, the
/// GitHub CLI's session, a personal access token). Shared by onboarding and Settings › GitHub.
/// </summary>
public sealed partial class GitHubSignInViewModel : ViewModelBase, IDisposable
{
    public const string NewTokenUrl = "https://github.com/settings/tokens/new?scopes=repo,workflow,read:org&description=ForgeDesk";

    public const string GitHubCliUrl = "https://cli.github.com";

    private readonly IGitHubAccountService _accounts;
    private readonly IShellIntegration _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private CancellationTokenSource? _signInCts;
    private Task? _cliCheck;
    private bool _disposed;

    public GitHubSignInViewModel(IGitHubAccountService accounts, IShellIntegration shell, IUiDispatcher dispatcher, ILogger? logger = null)
    {
        _accounts = accounts;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger ?? NullLogger.Instance;
        Account = accounts.Current;
        _accounts.AccountChanged += OnAccountChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsSignedOut), nameof(Login), nameof(DisplayName), nameof(AvatarUrl), nameof(Initials),
        nameof(MethodText), nameof(ScopesText), nameof(HasScopes), nameof(Warning), nameof(HasWarning), nameof(IsUnverified), nameof(ProfileUrl))]
    public partial GitHubAccount? Account { get; private set; }

    public bool IsSignedIn => Account is not null;

    public bool IsSignedOut => Account is null;

    public string? Login => Account?.User.Login;

    /// <summary>The account's name, or its login when it has none.</summary>
    public string? DisplayName => Account is { } account ? (string.IsNullOrWhiteSpace(account.User.Name) ? account.User.Login : account.User.Name) : null;

    public string? AvatarUrl => Account?.User.AvatarUrl is { Length: > 0 } url ? url : null;

    public string Initials
    {
        get
        {
            var name = DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                return "?";
            }

            var words = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        }
    }

    public string? ProfileUrl => Account?.User.HtmlUrl;

    public string? MethodText => Account?.Method switch
    {
        GitHubAuthMethod.GitCredentialManager => "Signed in with your browser (Git Credential Manager)",
        GitHubAuthMethod.GitHubCli => "Signed in with the GitHub CLI",
        GitHubAuthMethod.PersonalAccessToken => "Signed in with a personal access token",
        _ => null,
    };

    public string? ScopesText => Account is { Scopes.Count: > 0 } account ? string.Join(", ", account.Scopes.Order(StringComparer.Ordinal)) : null;

    public bool HasScopes => ScopesText is not null;

    /// <summary>Missing scopes (classic tokens without "repo" or "workflow").</summary>
    public string? Warning => Account?.Warning;

    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    /// <summary>The session was restored offline and has not been checked with GitHub yet.</summary>
    public bool IsUnverified => Account is { IsVerified: false };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInWithCliCommand))]
    public partial bool IsGitHubCliAvailable { get; private set; }

    [ObservableProperty]
    public partial bool IsCheckingCli { get; private set; }

    /// <summary>The personal access token being typed (cleared after signing in).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInWithTokenCommand))]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTokenFormOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigningIn), nameof(IsSigningInWithBrowser), nameof(IsSigningInWithCli), nameof(IsSigningInWithToken), nameof(CanStartSignIn))]
    [NotifyCanExecuteChangedFor(nameof(SignInWithBrowserCommand), nameof(SignInWithCliCommand), nameof(SignInWithTokenCommand))]
    public partial GitHubSignInMethod? ActiveMethod { get; private set; }

    public bool IsSigningIn => ActiveMethod is not null;

    public bool CanStartSignIn => ActiveMethod is null;

    public bool IsSigningInWithBrowser => ActiveMethod == GitHubSignInMethod.Browser;

    public bool IsSigningInWithCli => ActiveMethod == GitHubSignInMethod.GitHubCli;

    public bool IsSigningInWithToken => ActiveMethod == GitHubSignInMethod.Token;

    /// <summary>Raised on the UI thread after a successful sign-in.</summary>
    public event EventHandler<GitHubAccount>? SignedIn;

    /// <summary>Checks (once) whether the GitHub CLI is installed and signed in.</summary>
    public Task InitializeAsync() => _cliCheck ??= CheckCliAsync();

    private async Task CheckCliAsync()
    {
        IsCheckingCli = true;
        try
        {
            IsGitHubCliAvailable = await _accounts.IsGitHubCliAvailableAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogDebug(ex, "Could not detect the GitHub CLI");
            IsGitHubCliAvailable = false;
        }
        finally
        {
            IsCheckingCli = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartSignIn))]
    private Task SignInWithBrowserAsync() =>
        SignInAsync(GitHubSignInMethod.Browser, token => _accounts.SignInWithGitCredentialManagerAsync(token));

    [RelayCommand(CanExecute = nameof(CanSignInWithCli))]
    private Task SignInWithCliAsync() =>
        SignInAsync(GitHubSignInMethod.GitHubCli, token => _accounts.SignInWithGitHubCliAsync(token));

    private bool CanSignInWithCli() => CanStartSignIn && IsGitHubCliAvailable;

    [RelayCommand(CanExecute = nameof(CanSignInWithToken))]
    private Task SignInWithTokenAsync()
    {
        var token = Token.Trim();
        return SignInAsync(GitHubSignInMethod.Token, cancellation => _accounts.SignInWithTokenAsync(token, cancellation));
    }

    private bool CanSignInWithToken() => CanStartSignIn && !string.IsNullOrWhiteSpace(Token);

    /// <summary>Stops waiting for a sign-in (the browser window may stay open).</summary>
    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    [RelayCommand]
    private void ToggleTokenForm() => IsTokenFormOpen = !IsTokenFormOpen;

    /// <summary>Opens GitHub's "New personal access token" page with the scopes ForgeDesk needs.</summary>
    [RelayCommand]
    private void CreateToken() => OpenUrl(NewTokenUrl);

    [RelayCommand]
    private void InstallGitHubCli() => OpenUrl(GitHubCliUrl);

    [RelayCommand]
    private void OpenProfile()
    {
        if (ProfileUrl is { } url)
        {
            OpenUrl(url);
        }
    }

    private async Task SignInAsync(GitHubSignInMethod method, Func<CancellationToken, Task<GitHubAccount>> signIn)
    {
        if (_disposed || IsSigningIn)
        {
            return;
        }

        _signInCts?.Dispose();
        var cts = _signInCts = new CancellationTokenSource();
        ActiveMethod = method;
        try
        {
            GitHubAccount? account = null;
            var succeeded = await RunAsync(async () => account = await signIn(cts.Token).ConfigureAwait(true), errorTitle: "Could not sign in to GitHub")
                .ConfigureAwait(true);
            if (succeeded && account is not null)
            {
                Token = string.Empty;
                IsTokenFormOpen = false;
                Account = account;
                SignedIn?.Invoke(this, account);
            }
        }
        finally
        {
            ActiveMethod = null;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            _shell.OpenUrl(url);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Error = ErrorInfo.From(ex, "Could not open the browser");
        }
    }

    private void OnAccountChanged(object? sender, GitHubAccount? account) => _dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Account = account;
            if (account is not null)
            {
                Error = null;
            }
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _accounts.AccountChanged -= OnAccountChanged;
        _signInCts?.Cancel();
        _signInCts?.Dispose();
    }
}
