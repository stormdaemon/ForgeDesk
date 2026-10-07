using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>Base of the Git tab's small forms: a title, Confirm / Cancel, and a close request carrying the result.</summary>
public abstract partial class GitDialogViewModel : ObservableObject, IDialogViewModel
{
    public abstract string Title { get; }

    public virtual double PreferredWidth => 480;

    public double? PreferredHeight => null;

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

/// <summary>Asks for the name and email git records in commits, saved in the global git configuration.</summary>
public sealed partial class GitIdentityDialogViewModel : GitDialogViewModel
{
    public GitIdentityDialogViewModel(string? name = null, string? email = null)
    {
        Name = name ?? string.Empty;
        Email = email ?? string.Empty;
    }

    public override string Title => "Who is committing?";

    public string Explanation =>
        "Git records a name and an email address in every commit. They are saved in your global Git configuration, "
        + "so every repository on this computer uses them. Use the email of your GitHub account to link your commits to it.";

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    partial void OnNameChanged(string value) => ValidationMessage = null;

    partial void OnEmailChanged(string value) => ValidationMessage = null;

    protected override string? Validate()
    {
        var name = Name.Trim();
        var email = Email.Trim();
        if (name.Length == 0)
        {
            return "Enter your name.";
        }

        if (email.Length == 0 || email.Any(char.IsWhiteSpace) || !email.Contains('@', StringComparison.Ordinal)
            || email.StartsWith('@') || email.EndsWith('@'))
        {
            return "Enter a valid email address.";
        }

        return null;
    }
}

/// <summary>"Stash changes": an optional message and whether new files are included.</summary>
public sealed partial class StashDialogViewModel : GitDialogViewModel
{
    public StashDialogViewModel(int changedFiles, int untrackedFiles, string? branch)
    {
        ChangedFiles = changedFiles;
        UntrackedFiles = untrackedFiles;
        Explanation = $"Sets {Format.Count(changedFiles, "changed file")} aside"
            + (branch is null ? string.Empty : $" from {branch}")
            + " and cleans the working tree. Restore them later from the Stashes list.";
    }

    public override string Title => "Stash changes";

    public int ChangedFiles { get; }

    public int UntrackedFiles { get; }

    public string Explanation { get; }

    public bool HasUntrackedFiles => UntrackedFiles > 0;

    public string IncludeUntrackedText => $"Include new files ({Format.Count(UntrackedFiles, "untracked file")})";

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IncludeUntracked { get; set; } = true;

    protected override string? Validate() => null;
}

/// <summary>"Create tag": a validated name, an optional message (annotated tag) and the target commit.</summary>
public sealed partial class CreateTagDialogViewModel : GitDialogViewModel
{
    private readonly IReadOnlyCollection<string> _existing;

    public CreateTagDialogViewModel(string targetDescription, IReadOnlyCollection<string> existingTags, string? suggestedName = null)
    {
        TargetDescription = targetDescription;
        _existing = existingTags;
        Name = suggestedName ?? string.Empty;
    }

    public override string Title => "Create tag";

    /// <summary>"HEAD · a1b2c3d Fix login" or "a1b2c3d Fix login".</summary>
    public string TargetDescription { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    public string KindHint => string.IsNullOrWhiteSpace(Message)
        ? "Without a message the tag is lightweight: just a name for the commit."
        : "With a message the tag is annotated: it records who tagged, when, and why.";

    partial void OnNameChanged(string value) => ValidationMessage = null;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(KindHint));

    protected override string? Validate() => TagNames.Validate(Name, _existing);
}
