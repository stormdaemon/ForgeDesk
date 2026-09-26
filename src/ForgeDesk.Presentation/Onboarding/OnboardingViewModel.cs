using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Settings;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Onboarding;

public enum OnboardingStep
{
    Welcome,
    Environment,
    GitHub,
    FirstProject,
    Ready,
}

/// <summary>A progress dot of the step flow.</summary>
public sealed partial class OnboardingStepItem : ObservableObject
{
    public OnboardingStepItem(OnboardingStep step, string title)
    {
        Step = step;
        Title = title;
    }

    public OnboardingStep Step { get; }

    public string Title { get; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }
}

/// <summary>A value proposition of the welcome step.</summary>
public sealed record OnboardingHighlight(string Icon, string Title, string Description);

/// <summary>A keyboard tip of the last step.</summary>
public sealed record OnboardingTip(string Keys, string Description);

/// <summary>
/// First-launch flow: welcome, environment check (Git, GitHub CLI), GitHub sign-in (optional), a
/// first project (optional) and a few tips. Finishing — or skipping — marks onboarding completed and
/// opens the dashboard, or the project just added.
/// </summary>
public sealed partial class OnboardingViewModel : ViewModelBase, INavigationAware, IDisposable
{
    public const string DownloadGitUrl = "https://git-scm.com/download/win";

    private readonly ISettingsService _settings;
    private readonly IGitService _git;
    private readonly IGitHubAccountService _accounts;
    private readonly IProjectRegistry _registry;
    private readonly IDialogService _dialogs;
    private readonly CloneRepositoryFlow _clone;
    private readonly INavigationService _navigation;
    private readonly IShellIntegration _shell;
    private readonly INotificationService _notifications;
    private readonly ILogger<OnboardingViewModel> _logger;
    private bool _environmentChecked;
    private bool _disposed;

    public OnboardingViewModel(
        ISettingsService settings,
        IGitService git,
        IGitHubAccountService accounts,
        IProjectRegistry registry,
        IDialogService dialogs,
        CloneRepositoryFlow clone,
        INavigationService navigation,
        IShellIntegration shell,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<OnboardingViewModel> logger)
    {
        _settings = settings;
        _git = git;
        _accounts = accounts;
        _registry = registry;
        _dialogs = dialogs;
        _clone = clone;
        _navigation = navigation;
        _shell = shell;
        _notifications = notifications;
        _logger = logger;

        SignIn = new GitHubSignInViewModel(accounts, shell, dispatcher, logger);
        SignIn.SignedIn += OnSignedIn;
        SignIn.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitHubSignInViewModel.Account))
            {
                OnPropertyChanged(nameof(NextText));
            }
        };

        Steps =
        [
            new OnboardingStepItem(OnboardingStep.Welcome, "Welcome"),
            new OnboardingStepItem(OnboardingStep.Environment, "Environment"),
            new OnboardingStepItem(OnboardingStep.GitHub, "GitHub"),
            new OnboardingStepItem(OnboardingStep.FirstProject, "First project"),
            new OnboardingStepItem(OnboardingStep.Ready, "Ready"),
        ];
        UpdateSteps();
    }

    public IReadOnlyList<OnboardingStepItem> Steps { get; }

    public IReadOnlyList<OnboardingHighlight> Highlights { get; } =
    [
        new("Warning20", "See what needs you", "Every project at a glance: uncommitted work, branches behind, failing CI, open tasks."),
        new("BranchFork20", "Git and GitHub in one place", "Commit, sync, review issues and pull requests, and follow Actions without leaving the app."),
        new("Play20", "Run everything", "Detected dev, build and test commands, an integrated terminal and a task board per project."),
    ];

    public IReadOnlyList<OnboardingTip> Tips { get; } =
    [
        new("Ctrl+K", "Search projects, files and actions from anywhere"),
        new("Ctrl+O", "Add a local project folder"),
        new("Ctrl+1", "Switch between the tabs of a project (Ctrl+1 … Ctrl+0)"),
        new("F5", "Refresh the current view"),
    ];

    /// <summary>The account step (shared with Settings › GitHub).</summary>
    public GitHubSignInViewModel SignIn { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepIndex), nameof(IsWelcomeStep), nameof(IsEnvironmentStep), nameof(IsGitHubStep), nameof(IsFirstProjectStep),
        nameof(IsReadyStep), nameof(CanGoBack), nameof(NextText), nameof(StepCaption), nameof(ShowSkipSetup))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial OnboardingStep CurrentStep { get; private set; }

    public int StepIndex => (int)CurrentStep;

    public string StepCaption => $"Step {StepIndex + 1} of {Steps.Count}";

    public bool IsWelcomeStep => CurrentStep == OnboardingStep.Welcome;

    public bool IsEnvironmentStep => CurrentStep == OnboardingStep.Environment;

    public bool IsGitHubStep => CurrentStep == OnboardingStep.GitHub;

    public bool IsFirstProjectStep => CurrentStep == OnboardingStep.FirstProject;

    public bool IsReadyStep => CurrentStep == OnboardingStep.Ready;

    public bool CanGoBack => CurrentStep > OnboardingStep.Welcome;

    public bool ShowSkipSetup => CurrentStep < OnboardingStep.Ready;

    /// <summary>Label of the primary button.</summary>
    public string NextText => CurrentStep switch
    {
        OnboardingStep.Welcome => "Get started",
        OnboardingStep.GitHub => SignIn.IsSignedIn ? "Continue" : "Skip for now",
        OnboardingStep.FirstProject => AddedProject is null ? "Skip for now" : "Continue",
        OnboardingStep.Ready => "Open ForgeDesk",
        _ => "Continue",
    };

    // ----- Environment ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitFound), nameof(GitStatusText), nameof(GitTone), nameof(IsGitTooOld), nameof(ShowGitGuidance))]
    public partial GitInstallation? Git { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitStatusText), nameof(GitTone), nameof(ShowGitGuidance), nameof(GitHubCliStatusText))]
    public partial bool IsCheckingEnvironment { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitHubCliStatusText))]
    public partial bool IsGitHubCliAvailable { get; private set; }

    public bool IsGitFound => Git is not null;

    public bool IsGitTooOld => Git is { } git && Version.TryParse(git.Version, out var version) && version < GitSettingsViewModel.MinimumGitVersion;

    /// <summary>Git is missing or too old: the step explains how to install it.</summary>
    public bool ShowGitGuidance => !IsCheckingEnvironment && (!IsGitFound || IsGitTooOld);

    public string GitStatusText => IsCheckingEnvironment
        ? "Looking for Git…"
        : Git switch
        {
            null => "Git is not installed, or ForgeDesk can't find it.",
            { } found when IsGitTooOld => $"Git {found.Version} is too old. ForgeDesk needs Git {GitSettingsViewModel.MinimumGitVersion} or later.",
            { } found => $"Git {found.Version} is ready.",
        };

    public StatusTone GitTone => IsCheckingEnvironment ? StatusTone.Running : !IsGitFound ? StatusTone.Danger : IsGitTooOld ? StatusTone.Warning : StatusTone.Success;

    public string GitHubCliStatusText => IsCheckingEnvironment
        ? "Looking for the GitHub CLI…"
        : IsGitHubCliAvailable
            ? "The GitHub CLI is installed and signed in: you can reuse its session."
            : "The GitHub CLI isn't installed or signed in. It's optional.";

    // ----- First project ----------------------------------------------------------------------

    /// <summary>The project added (or cloned) during onboarding; opened when onboarding ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddedProject), nameof(AddedProjectText), nameof(NextText))]
    public partial Project? AddedProject { get; private set; }

    public bool HasAddedProject => AddedProject is not null;

    public string? AddedProjectText => AddedProject is { } project ? $"{project.Name} is ready — it opens when you finish." : null;

    [ObservableProperty]
    public partial bool IsAddingProject { get; private set; }

    // ----- Navigation -------------------------------------------------------------------------

    /// <summary>Every visit starts from the welcome step ("Run onboarding again" in Settings).</summary>
    public Task OnNavigatedToAsync(object? argument)
    {
        CurrentStep = OnboardingStep.Welcome;
        AddedProject = null;
        Error = null;
        return SignIn.InitializeAsync();
    }

    public void OnNavigatedFrom()
    {
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (CurrentStep == OnboardingStep.Ready)
        {
            await FinishAsync().ConfigureAwait(true);
            return;
        }

        await GoToAsync(CurrentStep + 1).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task BackAsync() => CanGoBack ? GoToAsync(CurrentStep - 1) : Task.CompletedTask;

    /// <summary>Leaves onboarding now; everything can be set up later in Settings.</summary>
    [RelayCommand]
    private Task SkipSetupAsync() => FinishAsync();

    internal async Task GoToAsync(OnboardingStep step)
    {
        CurrentStep = step;
        UpdateSteps();
        if (step == OnboardingStep.Environment && !_environmentChecked)
        {
            await CheckEnvironmentAsync().ConfigureAwait(true);
        }
    }

    private void UpdateSteps()
    {
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Step == CurrentStep;
            item.IsDone = item.Step < CurrentStep;
        }
    }

    // ----- Environment actions ----------------------------------------------------------------

    [RelayCommand]
    private async Task CheckEnvironmentAsync()
    {
        IsCheckingEnvironment = true;
        try
        {
            var gitTask = FindGitAsync();
            var cliTask = IsCliAvailableAsync();
            await Task.WhenAll(gitTask, cliTask).ConfigureAwait(true);
            Git = gitTask.Result;
            IsGitHubCliAvailable = cliTask.Result;
            _environmentChecked = true;
        }
        finally
        {
            IsCheckingEnvironment = false;
        }
    }

    private async Task<GitInstallation?> FindGitAsync()
    {
        try
        {
            return await _git.FindGitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogWarning(ex, "Git detection failed during onboarding");
            return null;
        }
    }

    private async Task<bool> IsCliAvailableAsync()
    {
        try
        {
            return await _accounts.IsGitHubCliAvailableAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _logger.LogDebug(ex, "GitHub CLI detection failed during onboarding");
            return false;
        }
    }

    [RelayCommand]
    private void DownloadGit() => OpenUrl(DownloadGitUrl);

    [RelayCommand]
    private void InstallGitHubCli() => OpenUrl(GitHubSignInViewModel.GitHubCliUrl);

    // ----- First project actions --------------------------------------------------------------

    [RelayCommand]
    private async Task AddLocalFolderAsync()
    {
        string? folder;
        try
        {
            folder = await _dialogs.PickFolderAsync("Choose a project folder", CloneDestination.BaseFolderFrom(_settings.Current.DefaultCloneDirectory))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Error = ErrorInfo.From(ex, "Could not open the folder picker");
            return;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        IsAddingProject = true;
        try
        {
            var added = await RunAsync(async () =>
            {
                AddedProject = await _registry.FindByPathAsync(folder).ConfigureAwait(true) ?? await AddAsync(folder).ConfigureAwait(true);
            }, errorTitle: "Could not add this folder").ConfigureAwait(true);
            if (added)
            {
                await GoToAsync(OnboardingStep.Ready).ConfigureAwait(true);
            }
        }
        finally
        {
            IsAddingProject = false;
        }
    }

    private async Task<Project> AddAsync(string folder)
    {
        try
        {
            return await _registry.AddAsync(folder).ConfigureAwait(true);
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.AlreadyExists)
        {
            // Registered under an equivalent spelling of the path (case, trailing separator…).
            var existing = await _registry.FindByPathAsync(folder).ConfigureAwait(true);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    [RelayCommand]
    private async Task CloneAsync()
    {
        Error = null;
        Project? project;
        try
        {
            project = await _clone.CloneAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Error = ErrorInfo.From(ex, "Could not clone the repository");
            return;
        }

        if (project is not null)
        {
            AddedProject = project;
            await GoToAsync(OnboardingStep.Ready).ConfigureAwait(true);
        }
    }

    // ----- Finish -----------------------------------------------------------------------------

    private async Task FinishAsync()
    {
        try
        {
            await _settings.UpdateAsync(s => s with { OnboardingCompleted = true }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            // Still let the user in: onboarding simply shows again next time.
            _logger.LogWarning(ex, "Could not save that onboarding is completed");
            _notifications.ShowError(ErrorInfo.From(ex, "Could not save your setup"));
        }

        if (AddedProject is { } project)
        {
            try
            {
                await _navigation.OpenProjectAsync(project.Id).ConfigureAwait(true);
                return;
            }
            catch (Exception ex) when (!ex.IsCancellation())
            {
                _logger.LogWarning(ex, "Could not open {ProjectId} after onboarding", project.Id);
            }
        }

        _navigation.GoToDashboard();
    }

    private void OnSignedIn(object? sender, GitHubAccount account) => OnPropertyChanged(nameof(NextText));

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SignIn.SignedIn -= OnSignedIn;
        SignIn.Dispose();
    }
}
