using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>A segment of the language bar (and its legend entry).</summary>
public sealed record LanguageSegmentViewModel(string Name, double Percentage, string Color)
{
    public string PercentText => Percentage >= 10
        ? Percentage.ToString("0", CultureInfo.CurrentCulture) + " %"
        : Percentage.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string ToolTip => $"{Name} · {PercentText}";
}

/// <summary>Hero row: description from the README, path, GitHub link, technologies and languages.</summary>
public sealed partial class OverviewHeroViewModel : OverviewCardViewModel
{
    internal const int MaxLanguages = 5;
    internal const int MaxTechnologies = 10;
    internal const string OtherLanguageColor = "#8B949E";

    private readonly IFileService _files;
    private readonly IShellIntegration _shell;
    private readonly INotificationService _notifications;

    public OverviewHeroViewModel(ProjectContext context, IFileService files, IShellIntegration shell, INotificationService notifications)
        : base(context)
    {
        _files = files;
        _shell = shell;
        _notifications = notifications;
        Context.PropertyChanged += OnContextPropertyChanged;
        ApplyProfile();
    }

    public string Path => Context.Root;

    public string? GitHubUrl => Context.Project.GitHub?.HtmlUrl;

    public string? GitHubName => Context.Project.GitHub?.FullName;

    public bool HasGitHub => GitHubUrl is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string? Description { get; private set; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>Relative path of the README the description comes from.</summary>
    [ObservableProperty]
    public partial string? ReadmePath { get; private set; }

    public ObservableCollection<string> Technologies { get; } = [];

    public ObservableCollection<LanguageSegmentViewModel> Languages { get; } = [];

    [ObservableProperty]
    public partial bool HasLanguages { get; private set; }

    [ObservableProperty]
    public partial bool HasTechnologies { get; private set; }

    protected override string ErrorTitle => "Could not read the README";

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        ApplyProfile();
        var readme = FindReadme(Context.Profile);
        string? description = null;
        string? path = null;
        if (Context.FolderExists)
        {
            foreach (var candidate in readme is null ? DefaultReadmes : [readme])
            {
                try
                {
                    var content = await _files.ReadAsync(Context.Root, candidate, 256 * 1024, cancellationToken).ConfigureAwait(true);
                    if (content.Text is { } text)
                    {
                        description = ReadmeSummary.Extract(text);
                        path = candidate;
                        break;
                    }
                }
                catch (ForgeException ex) when (ex.Kind is ErrorKind.NotFound or ErrorKind.PathNotFound)
                {
                    // Try the next name.
                }
                catch (FileNotFoundException)
                {
                }
            }
        }

        Description = description;
        ReadmePath = path;
    }

    private static readonly string[] DefaultReadmes = ["README.md", "readme.md", "Readme.md", "README", "README.txt", "README.rst"];

    internal static string? FindReadme(ProjectProfile? profile) =>
        profile?.ImportantFiles.FirstOrDefault(f => !f.Contains('/', StringComparison.Ordinal)
            && System.IO.Path.GetFileNameWithoutExtension(f).Equals("readme", StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            _shell.OpenFolder(Context.Root);
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the folder"));
        }
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        if (GitHubUrl is not { } url)
        {
            return;
        }

        try
        {
            _shell.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }

    [RelayCommand]
    private void OpenReadme() => Context.RequestNavigation(WorkspaceSection.Files, ReadmePath ?? "README.md");

    private void ApplyProfile()
    {
        var profile = Context.Profile;
        var technologies = SelectTechnologies(profile);
        if (!technologies.SequenceEqual(Technologies, StringComparer.Ordinal))
        {
            Technologies.Clear();
            foreach (var technology in technologies)
            {
                Technologies.Add(technology);
            }
        }

        HasTechnologies = Technologies.Count > 0;

        var segments = BuildLanguageSegments(profile?.Languages ?? []);
        Languages.Clear();
        foreach (var segment in segments)
        {
            Languages.Add(segment);
        }

        HasLanguages = Languages.Count > 0;
    }

    internal static IReadOnlyList<string> SelectTechnologies(ProjectProfile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        return profile.Technologies
            .Where(t => t.Kind != TechnologyKind.Language)
            .OrderBy(t => t.Kind switch
            {
                TechnologyKind.Framework => 0,
                TechnologyKind.Runtime => 1,
                TechnologyKind.Library => 2,
                TechnologyKind.BuildTool => 3,
                TechnologyKind.TestFramework => 4,
                TechnologyKind.PackageManager => 5,
                _ => 6,
            })
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTechnologies)
            .ToList();
    }

    /// <summary>The top languages, plus "Other" for the rest, as percentages that add up to 100.</summary>
    internal static IReadOnlyList<LanguageSegmentViewModel> BuildLanguageSegments(IReadOnlyList<LanguageShare> languages)
    {
        var total = languages.Sum(l => Math.Max(0, l.Percentage));
        if (total <= 0)
        {
            return [];
        }

        var ordered = languages.Where(l => l.Percentage > 0).OrderByDescending(l => l.Percentage).ToList();
        var segments = ordered.Take(MaxLanguages)
            .Select(l => new LanguageSegmentViewModel(l.Language, l.Percentage * 100 / total, NormalizeColor(l.Color)))
            .ToList();
        var rest = ordered.Skip(MaxLanguages).Sum(l => l.Percentage);
        if (rest > 0)
        {
            segments.Add(new LanguageSegmentViewModel("Other", rest * 100 / total, OtherLanguageColor));
        }

        return segments;
    }

    private static string NormalizeColor(string? color) =>
        color is { Length: 7 or 9 } && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit) ? color : OtherLanguageColor;

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectContext.Profile):
                ApplyProfile();
                if (FindReadme(Context.Profile) is { } readme && !string.Equals(readme, ReadmePath, StringComparison.Ordinal))
                {
                    RequestReload();
                }

                break;
            case nameof(ProjectContext.Project):
                OnPropertyChanged(nameof(Path));
                OnPropertyChanged(nameof(GitHubUrl));
                OnPropertyChanged(nameof(GitHubName));
                OnPropertyChanged(nameof(HasGitHub));
                RequestReload();
                break;
        }
    }

    protected override void OnDispose() => Context.PropertyChanged -= OnContextPropertyChanged;
}
