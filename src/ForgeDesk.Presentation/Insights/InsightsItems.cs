using System.Globalization;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Insights;

/// <summary>A row of the languages table.</summary>
public sealed record InsightsLanguageViewModel(string Name, int Files, long Lines, long Bytes, string Color, double Share)
{
    public string FilesText => Files.ToString("N0", CultureInfo.CurrentCulture);

    public string LinesText => Lines.ToString("N0", CultureInfo.CurrentCulture);

    public string ShareText => (Share * 100).ToString(Share >= 0.1 ? "0" : "0.0", CultureInfo.CurrentCulture) + " %";

    /// <summary>Unused part of the share bar (0..1).</summary>
    public double Remainder => Math.Max(0, 1 - Share);
}

/// <summary>A row of the dependencies list: a manifest header or a dependency.</summary>
public abstract class InsightsDependencyRowViewModel
{
    public abstract bool IsHeader { get; }
}

public sealed class InsightsDependencyGroupViewModel(string ecosystem, string manifest, int count, int devCount) : InsightsDependencyRowViewModel
{
    public override bool IsHeader => true;

    public string Ecosystem { get; } = ecosystem;

    public string Manifest { get; } = manifest;

    public int Count { get; } = count;

    public int DevCount { get; } = devCount;

    public string CountText => DevCount > 0 ? $"{Format.Count(Count, "package")} · {DevCount} dev" : Format.Count(Count, "package");
}

public sealed class InsightsDependencyViewModel(DependencyInfo dependency) : InsightsDependencyRowViewModel
{
    public override bool IsHeader => false;

    public DependencyInfo Dependency { get; } = dependency;

    public string Name => Dependency.Name;

    public string? Version => string.IsNullOrWhiteSpace(Dependency.Version) ? null : Dependency.Version;

    public bool IsDevelopment => Dependency.IsDevelopment;

    public string Manifest => Dependency.Manifest;

    public string ToolTip => $"{Name} {Version}\n{Dependency.Ecosystem} · {Manifest}{(IsDevelopment ? " · development only" : string.Empty)}";
}

/// <summary>An important-file check ("README", "LICENSE"…): present or missing, and why it matters.</summary>
public sealed record InsightsHygieneItemViewModel(string Label, bool Present, string? Path, string Why)
{
    public StatusTone Tone => Present ? StatusTone.Success : StatusTone.Warning;

    public string Icon => Present ? "CheckmarkCircle20" : "DismissCircle20";

    public bool CanOpen => Present && !string.IsNullOrEmpty(Path);

    public string ToolTip => Present ? $"{Path}\nClick to open it in Files" : $"Missing: {Why}";
}

/// <summary>Count of TODO markers of one tag.</summary>
public sealed record InsightsTodoTagViewModel(string Tag, int Count)
{
    public StatusTone Tone => Tag.ToUpperInvariant() switch
    {
        "FIXME" or "BUG" or "XXX" => StatusTone.Danger,
        "HACK" => StatusTone.Warning,
        _ => StatusTone.Info,
    };
}

public sealed record InsightsTodoViewModel(string Path, int Line, string Tag, string Text)
{
    public string Location => $"{Path}:{Line.ToString(CultureInfo.InvariantCulture)}";

    public string ToolTip => $"{Location}\n{Tag}: {Text}\nClick to open the file";
}

public sealed record InsightsLargeFileViewModel(string Path, long Bytes, double Ratio)
{
    public string SizeText => Format.Bytes(Bytes);

    public double Remainder => Math.Max(0, 1 - Ratio);

    public string FileName => System.IO.Path.GetFileName(Path);

    public string? Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') is { Length: > 0 } dir ? dir : null;
}

/// <summary>One week of the 12-week commit chart.</summary>
public sealed record InsightsWeekViewModel(DateOnly WeekStart, int Commits, double Ratio, bool IsCurrent)
{
    public double Remainder => Math.Max(0, 1 - Ratio);

    public string Label => WeekStart.ToString("d MMM", CultureInfo.CurrentCulture);

    public string ToolTip => IsCurrent
        ? $"{Format.Count(Commits, "commit")} this week"
        : $"{Format.Count(Commits, "commit")} the week of {WeekStart.ToString("d MMMM", CultureInfo.CurrentCulture)}";
}

public sealed record InsightsContributorViewModel(string Name, int Commits, double Ratio)
{
    public string Initials => Overview.OverviewCommitViewModel.InitialsOf(Name);

    public string CommitsText => Format.Count(Commits, "commit");

    public double Remainder => Math.Max(0, 1 - Ratio);
}

/// <summary>A count of the repository state ("3 unpushed commits") that opens the Git tab.</summary>
public sealed record InsightsRepositoryFactViewModel(string Label, int Value, StatusTone Tone, string Icon)
{
    public string ValueText => Value.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A branch listed as stale or merged.</summary>
public sealed record InsightsBranchViewModel(string Name, bool IsMerged)
{
    public string ToolTip => IsMerged ? $"{Name} is fully merged and can be deleted" : $"{Name} has had no commit for a long time";
}
