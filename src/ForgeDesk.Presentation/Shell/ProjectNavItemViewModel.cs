using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Shell;

/// <summary>A project in the sidebar: avatar, name, branch, status dot and running indicator.</summary>
public sealed partial class ProjectNavItemViewModel : ObservableObject
{
    private readonly IProjectActions _actions;

    public ProjectNavItemViewModel(Project project, IProjectActions actions)
    {
        ArgumentNullException.ThrowIfNull(project);
        _actions = actions;
        Project = project;
    }

    public string Id => Project.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Path), nameof(Color), nameof(IsPinned), nameof(PinMenuText), nameof(ToolTip))]
    public partial Project Project { get; private set; }

    public string Name => Project.Name;

    public string Path => Project.Path;

    /// <summary>#RRGGBB avatar color, or null for the color derived from the name.</summary>
    public string? Color => Project.Color;

    public bool IsPinned => Project.IsPinned;

    public string PinMenuText => IsPinned ? "Unpin" : "Pin to sidebar";

    /// <summary>True once a status snapshot (cached or fresh) is known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(SecondaryText))]
    public partial bool HasSnapshot { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryText))]
    public partial string? Branch { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryText))]
    public partial bool IsGitRepository { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial int ChangedFiles { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    public partial AttentionLevel Attention { get; private set; }

    /// <summary>The most important reason the project needs attention ("CI failing on main").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string? AttentionMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(SecondaryText), nameof(ToolTip))]
    public partial bool IsFolderMissing { get; private set; }

    /// <summary>At least one command of this project is running.</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>The project is the page on screen.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public StatusTone Status
    {
        get
        {
            if (!HasSnapshot)
            {
                return StatusTone.None;
            }

            if (IsFolderMissing)
            {
                return StatusTone.Warning;
            }

            return Attention switch
            {
                AttentionLevel.Critical => StatusTone.Danger,
                AttentionLevel.Warning => StatusTone.Warning,
                AttentionLevel.Info => StatusTone.Info,
                _ => StatusTone.Neutral,
            };
        }
    }

    /// <summary>Second line of the entry: the branch, or why there is none.</summary>
    public string SecondaryText
    {
        get
        {
            if (IsFolderMissing)
            {
                return "Folder not found";
            }

            if (!HasSnapshot)
            {
                return string.Empty;
            }

            return Branch ?? (IsGitRepository ? "Detached HEAD" : "Not a Git repository");
        }
    }

    public string ToolTip
    {
        get
        {
            var lines = new List<string>(4) { Name, Path };
            if (IsFolderMissing)
            {
                lines.Add("The folder no longer exists.");
            }
            else if (ChangedFiles > 0)
            {
                lines.Add(Format.Count(ChangedFiles, "changed file"));
            }

            if (AttentionMessage is { } attention)
            {
                lines.Add(attention);
            }

            return string.Join('\n', lines);
        }
    }

    [RelayCommand]
    private Task OpenAsync() => _actions.OpenAsync(Id);

    [RelayCommand]
    private void OpenInExplorer() => _actions.OpenInExplorer(Path);

    [RelayCommand]
    private Task TogglePinAsync() => _actions.TogglePinAsync(Id);

    [RelayCommand]
    private Task RenameAsync() => _actions.RenameAsync(Id);

    [RelayCommand]
    private Task RemoveAsync() => _actions.RemoveAsync(Id);

    internal void Update(Project project)
    {
        if (project != Project)
        {
            Project = project;
        }
    }

    internal void Apply(ProjectSnapshot snapshot)
    {
        IsFolderMissing = !snapshot.FolderExists;
        IsGitRepository = snapshot.IsGitRepository;
        Branch = snapshot.Branch;
        ChangedFiles = snapshot.ChangedFiles;
        Attention = snapshot.AttentionLevel;
        AttentionMessage = snapshot.Attention.OrderByDescending(a => a.Level).FirstOrDefault()?.Message ?? snapshot.Problem;
        HasSnapshot = true;
    }
}
