namespace ForgeDesk.Core.Settings;

public enum ThemePreference
{
    System,
    Dark,
    Light,
}

public enum PullStrategy
{
    /// <summary>git pull --ff-only (never creates merge commits; fails when diverged).</summary>
    FastForwardOnly,
    Merge,
    Rebase,
}

/// <summary>
/// All user preferences. Immutable: change with <c>with</c> expressions and save through
/// <see cref="ISettingsService"/>. New properties must have safe defaults so that settings
/// written by older versions keep loading.
/// </summary>
public sealed record AppSettings
{
    public static readonly AppSettings Default = new();

    // General
    public bool OnboardingCompleted { get; init; }
    public string? DefaultCloneDirectory { get; init; }
    public bool ConfirmBeforeClosingWithRunningTasks { get; init; } = true;
    public bool RestoreLastProjectOnStartup { get; init; } = true;
    public string? LastOpenedProjectId { get; init; }

    // Appearance
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool UseSystemAccent { get; init; }
    public double CodeFontSize { get; init; } = 13;
    public string CodeFontFamily { get; init; } = "Cascadia Code, Cascadia Mono, Consolas";

    // Git
    public string? GitExecutablePath { get; init; }
    public PullStrategy PullStrategy { get; init; } = PullStrategy.FastForwardOnly;
    public bool AutoFetch { get; init; } = true;
    public int AutoFetchIntervalMinutes { get; init; } = 10;
    public bool UseGitHubTokenForGit { get; init; } = true;

    // GitHub
    public string? GitHubLogin { get; init; }
    public bool GitHubEnabled { get; init; } = true;

    // Terminal
    public string? DefaultShellId { get; init; }
    public double TerminalFontSize { get; init; } = 13;

    // Runs
    public bool NotifyWhenRunCompletes { get; init; } = true;
    public int RunHistoryPerProject { get; init; } = 200;
    public int StalledRunWarningMinutes { get; init; } = 5;

    // Editor integration
    public string? ExternalEditorCommand { get; init; }

    // Updates
    public bool CheckForUpdatesAutomatically { get; init; } = true;

    // Dashboard
    public string DashboardSort { get; init; } = "attention";
    public string DashboardLayout { get; init; } = "grid";
}
