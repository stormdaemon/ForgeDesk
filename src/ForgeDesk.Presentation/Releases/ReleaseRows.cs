using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Releases;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Releases;

/// <summary>
/// Deep link into the Releases tab: <see cref="StartNewRelease"/> opens the wizard, optionally for
/// an existing local tag (<see cref="Version"/>).
/// </summary>
public sealed record ReleasesNavigation(bool StartNewRelease, string? Version = null)
{
    public static ReleasesNavigation NewRelease(string? version = null) => new(true, version);
}

/// <summary>A downloadable file of a release.</summary>
public sealed class ReleaseAssetViewModel
{
    public ReleaseAssetViewModel(GitHubReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Name = asset.Name;
        Size = asset.Size;
        DownloadCount = asset.DownloadCount;
        DownloadUrl = asset.DownloadUrl;
    }

    public string Name { get; }

    public long Size { get; }

    public string SizeText => Format.Bytes(Size);

    public int DownloadCount { get; }

    public string DownloadsText => Format.Count(DownloadCount, "download");

    public string DownloadUrl { get; }

    public string ToolTip => $"Download {Name} ({SizeText})";
}

/// <summary>A release in the list, with its assets and expandable notes.</summary>
public sealed partial class ReleaseRowViewModel : ObservableObject
{
    public ReleaseRowViewModel(GitHubRelease release, bool isLatest)
    {
        ArgumentNullException.ThrowIfNull(release);
        Release = release;
        IsLatest = isLatest;
        Assets = release.Assets.Select(a => new ReleaseAssetViewModel(a)).ToList();
    }

    public GitHubRelease Release { get; }

    public long Id => Release.Id;

    public string TagName => Release.TagName;

    /// <summary>The release title, or its tag when it has none.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Release.Name) ? Release.TagName : Release.Name!;

    /// <summary>The most recent published, non-pre-release release (what GitHub calls "Latest").</summary>
    public bool IsLatest { get; }

    public bool IsPrerelease => Release.IsPrerelease;

    public bool IsDraft => Release.IsDraft;

    public DateTimeOffset Date => Release.PublishedAt ?? Release.CreatedAt;

    /// <summary>"Published" or "Created" (drafts are not published yet).</summary>
    public string DateLabel => Release.PublishedAt is null ? "Created" : "Published";

    public string? Author => Release.Author;

    public bool HasAuthor => !string.IsNullOrWhiteSpace(Release.Author);

    public string? Target => Release.TargetCommitish;

    public string HtmlUrl => Release.HtmlUrl;

    public IReadOnlyList<ReleaseAssetViewModel> Assets { get; }

    public bool HasAssets => Assets.Count > 0;

    public string AssetsSummary => HasAssets
        ? $"{Format.Count(Assets.Count, "asset")} · {Format.Count(Assets.Sum(a => a.DownloadCount), "download")}"
        : "No assets";

    public string Notes => Release.Body;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Release.Body);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>A candidate version of the wizard's first step (Patch, Minor, Major…).</summary>
public sealed partial class VersionOptionViewModel : ObservableObject
{
    public VersionOptionViewModel(VersionSuggestion suggestion, string tagPrefix, bool isRecommended)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        Label = suggestion.Label;
        Version = suggestion.Version;
        Reason = suggestion.Reason;
        TagName = tagPrefix + suggestion.Version;
        IsRecommended = isRecommended;
    }

    public string Label { get; }

    public string Version { get; }

    public string Reason { get; }

    public string TagName { get; }

    public bool IsRecommended { get; }

    public string AutomationId => "Releases.Version." + Label.Replace(" ", string.Empty, StringComparison.Ordinal);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>What the release tags: the current branch's tip or the exact HEAD commit.</summary>
public sealed record ReleaseTargetOption(string Title, string Value, string Description);

/// <summary>A file attached to the release being prepared.</summary>
public sealed partial class ReleaseAssetFileViewModel : ObservableObject
{
    public ReleaseAssetFileViewModel(string path, bool isSuggested)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        IsSuggested = isSuggested;

        // Suggestions are only candidates: nothing is uploaded unless the user ticks it.
        IsSelected = !isSuggested;
        try
        {
            var info = new FileInfo(path);
            Exists = info.Exists;
            Size = info.Exists ? info.Length : 0;
            Modified = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Exists = false;
        }
    }

    public string Path { get; }

    public string Name { get; }

    public string? Directory => System.IO.Path.GetDirectoryName(Path);

    /// <summary>Found by ForgeDesk in a build output folder (vs. added by the user).</summary>
    public bool IsSuggested { get; }

    public bool Exists { get; }

    public long Size { get; }

    public string SizeText => Exists ? Format.Bytes(Size) : "Missing";

    public DateTimeOffset? Modified { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A step of the release execution timeline.</summary>
public sealed partial class ReleaseTimelineStepViewModel : ObservableObject
{
    public ReleaseTimelineStepViewModel(ReleaseStepUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Kind = update.Step;
        Title = update.Title;
        Apply(update);
    }

    public ReleaseStepKind Kind { get; }

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsPending), nameof(IsSucceeded), nameof(IsFailed), nameof(IsSkipped), nameof(StateIcon),
        nameof(StateTone), nameof(HasProgress), nameof(StateText))]
    public partial ReleaseStepState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string? Detail { get; private set; }

    /// <summary>0..1 for uploads in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressPercent))]
    public partial double? Progress { get; private set; }

    public bool IsPending => State == ReleaseStepState.Pending;

    public bool IsRunning => State == ReleaseStepState.Running;

    public bool IsSucceeded => State == ReleaseStepState.Succeeded;

    public bool IsFailed => State == ReleaseStepState.Failed;

    public bool IsSkipped => State == ReleaseStepState.Skipped;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public bool HasProgress => IsRunning && Progress is not null;

    public double ProgressPercent => Math.Clamp((Progress ?? 0) * 100, 0, 100);

    public string StateText => State switch
    {
        ReleaseStepState.Running => "In progress",
        ReleaseStepState.Succeeded => "Done",
        ReleaseStepState.Failed => "Failed",
        ReleaseStepState.Skipped => "Skipped",
        _ => "Waiting",
    };

    /// <summary>WPF-UI symbol (the running state shows a spinner instead).</summary>
    public string StateIcon => State switch
    {
        ReleaseStepState.Succeeded => "CheckmarkCircle16",
        ReleaseStepState.Failed => "DismissCircle16",
        ReleaseStepState.Skipped => "ArrowCircleRight16",
        _ => "Circle16",
    };

    public StatusTone StateTone => State switch
    {
        ReleaseStepState.Succeeded => StatusTone.Success,
        ReleaseStepState.Failed => StatusTone.Danger,
        ReleaseStepState.Running => StatusTone.Running,
        _ => StatusTone.Neutral,
    };

    internal void Apply(ReleaseStepUpdate update)
    {
        State = update.State;
        Detail = update.Detail;
        Progress = update.Progress;
    }
}

/// <summary>A step of the wizard's header (Version · Notes · Assets · Review).</summary>
public sealed partial class WizardStepViewModel : ObservableObject
{
    public WizardStepViewModel(ReleaseWizardStep step, int number, string title)
    {
        Step = step;
        Number = number;
        Title = title;
    }

    public ReleaseWizardStep Step { get; }

    public int Number { get; }

    public string NumberText => Number.ToString(CultureInfo.CurrentCulture);

    public string Title { get; }

    public string AutomationId => $"Releases.Step.{Step}";

    [ObservableProperty]
    public partial bool IsCurrent { get; internal set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; internal set; }

    /// <summary>Not the first step: a connector line is drawn before it.</summary>
    public bool HasConnector => Number > 1;
}
