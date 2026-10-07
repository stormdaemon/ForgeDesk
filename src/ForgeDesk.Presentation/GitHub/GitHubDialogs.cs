using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>Base of the GitHub tab's forms: a title, Confirm / Cancel, and a close request carrying the result.</summary>
public abstract partial class GitHubDialogViewModel : ObservableObject, IDialogViewModel
{
    public abstract string Title { get; }

    public virtual double PreferredWidth => 640;

    public virtual double? PreferredHeight => null;

    public event EventHandler<bool?>? CloseRequested;

    /// <summary>Validation message shown under the form, null when the input is valid.</summary>
    [ObservableProperty]
    public partial string? ValidationMessage { get; protected set; }

    [RelayCommand]
    private void Confirm()
    {
        ValidationMessage = Validate();
        if (ValidationMessage is null)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>Returns an error message, or null when the form can be submitted.</summary>
    protected abstract string? Validate();
}

/// <summary>"New issue": a title and a Markdown description.</summary>
public sealed partial class NewIssueDialogViewModel : GitHubDialogViewModel
{
    public NewIssueDialogViewModel(string repositoryName)
    {
        RepositoryName = repositoryName;
    }

    public override string Title => "New issue";

    public string RepositoryName { get; }

    [ObservableProperty]
    public partial string IssueTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    /// <summary>Shows the rendered Markdown instead of the editor.</summary>
    [ObservableProperty]
    public partial bool ShowPreview { get; set; }

    partial void OnIssueTitleChanged(string value) => ValidationMessage = null;

    protected override string? Validate() =>
        string.IsNullOrWhiteSpace(IssueTitle) ? "Give the issue a title." : null;
}

/// <summary>"New pull request": title, description, the branch to merge and the branch to merge into, draft.</summary>
public sealed partial class NewPullRequestDialogViewModel : GitHubDialogViewModel
{
    public NewPullRequestDialogViewModel(string repositoryName, IReadOnlyList<string> headBranches, IReadOnlyList<string> baseBranches, string? head, string? baseBranch)
    {
        ArgumentNullException.ThrowIfNull(headBranches);
        ArgumentNullException.ThrowIfNull(baseBranches);
        RepositoryName = repositoryName;
        HeadBranches = headBranches;
        BaseBranches = baseBranches;
        Head = head ?? headBranches.FirstOrDefault() ?? string.Empty;
        Base = baseBranch ?? baseBranches.FirstOrDefault() ?? string.Empty;
        PullRequestTitle = SuggestTitle(Head);
    }

    public override string Title => "New pull request";

    public string RepositoryName { get; }

    /// <summary>Local branches that can be proposed.</summary>
    public IReadOnlyList<string> HeadBranches { get; }

    /// <summary>Branches of the GitHub repository that can receive the changes.</summary>
    public IReadOnlyList<string> BaseBranches { get; }

    [ObservableProperty]
    public partial string PullRequestTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Head { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Base { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDraft { get; set; }

    [ObservableProperty]
    public partial bool ShowPreview { get; set; }

    /// <summary>"Merge feature/login into main".</summary>
    public string Summary => $"Merge {(Head.Length > 0 ? Head : "…")} into {(Base.Length > 0 ? Base : "…")}";

    partial void OnPullRequestTitleChanged(string value) => ValidationMessage = null;

    partial void OnHeadChanged(string value) => ValidationMessage = null;

    partial void OnBaseChanged(string value) => ValidationMessage = null;

    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(PullRequestTitle))
        {
            return "Give the pull request a title.";
        }

        if (string.IsNullOrWhiteSpace(Head))
        {
            return "Choose the branch with your changes.";
        }

        if (string.IsNullOrWhiteSpace(Base))
        {
            return "Choose the branch to merge into.";
        }

        return string.Equals(Head.Trim(), Base.Trim(), StringComparison.Ordinal)
            ? "A branch can't be merged into itself: choose another base branch."
            : null;
    }

    /// <summary>"feature/user-login" → "User login".</summary>
    internal static string SuggestTitle(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return string.Empty;
        }

        var name = branch[(branch.LastIndexOf('/') + 1)..].Replace('-', ' ').Replace('_', ' ').Trim();
        return name.Length == 0 ? string.Empty : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
