using System.Text.RegularExpressions;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.WorkItems;

/// <summary>Validation and normalization of user input for work items and their links.</summary>
internal static partial class WorkItemRules
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 20_000;
    public const int MaxLabels = 20;
    public const int MaxLabelLength = 40;
    public const int MaxLinksPerItem = 100;
    public const int MaxLinkValueLength = 2048;
    public const int MaxLinkLabelLength = 200;

    public static string NormalizeTitle(string? title)
    {
        var trimmed = CollapseWhitespace(title ?? string.Empty);
        if (trimmed.Length == 0)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Give the task a title.");
        }

        if (trimmed.Length > MaxTitleLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                $"The title is too long ({trimmed.Length} characters).",
                $"Keep it under {MaxTitleLength} characters and put the details in the description.");
        }

        return trimmed;
    }

    public static string NormalizeDescription(string? description)
    {
        var normalized = (description ?? string.Empty).Trim();
        if (normalized.Length > MaxDescriptionLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                "The description is too long.",
                $"Keep it under {MaxDescriptionLength:N0} characters, or link to a document instead.");
        }

        return normalized;
    }

    /// <summary>
    /// Trims labels, drops empty ones and case-insensitive duplicates (first spelling wins).
    /// Commas separate labels because they are stored comma-separated.
    /// </summary>
    public static IReadOnlyList<string> NormalizeLabels(IEnumerable<string>? labels)
    {
        if (labels is null)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in labels)
        {
            if (raw is null)
            {
                continue;
            }

            foreach (var part in raw.Split(','))
            {
                var label = CollapseWhitespace(part);
                if (label.Length == 0)
                {
                    continue;
                }

                if (label.Length > MaxLabelLength)
                {
                    throw new ForgeException(ErrorKind.InvalidInput,
                        $"The label \"{label[..20]}…\" is too long.",
                        $"Labels can have at most {MaxLabelLength} characters.");
                }

                if (seen.Add(label))
                {
                    result.Add(label);
                }
            }
        }

        if (result.Count > MaxLabels)
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                $"A task can have at most {MaxLabels} labels.",
                "Remove the labels you don't filter by.");
        }

        return result;
    }

    public static string SerializeLabels(IReadOnlyList<string> labels) => string.Join(',', labels);

    public static IReadOnlyList<string> DeserializeLabels(string? stored) =>
        string.IsNullOrEmpty(stored)
            ? []
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static void EnsureDefined(WorkItemStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown work item status.");
        }
    }

    public static void EnsureDefined(WorkItemPriority priority)
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown work item priority.");
        }
    }

    /// <summary>
    /// Canonical form of a link value, so equivalent inputs ("#12" / "12", "src\a.cs" / "src/a.cs",
    /// upper/lower-case hashes) are stored the same way and deduplicated.
    /// </summary>
    public static string NormalizeLinkValue(WorkItemLinkKind kind, string? value)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown link kind.");
        }

        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "The link is empty.", "Enter what the task should point to.");
        }

        if (trimmed.Length > MaxLinkValueLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "The link is too long.");
        }

        switch (kind)
        {
            case WorkItemLinkKind.Commit:
                if (!CommitHash().IsMatch(trimmed))
                {
                    throw new ForgeException(ErrorKind.InvalidInput,
                        $"\"{trimmed}\" is not a commit hash.",
                        "Copy the hash from the Git history, for example 3f2a9c1.");
                }

                return trimmed.ToLowerInvariant();

            case WorkItemLinkKind.File:
                var path = trimmed.Replace('\\', '/');
                while (path.StartsWith("./", StringComparison.Ordinal))
                {
                    path = path[2..];
                }

                return path;

            case WorkItemLinkKind.Issue:
            case WorkItemLinkKind.PullRequest:
                var number = IssueNumber().Match(trimmed);
                return number.Success ? number.Groups["n"].Value : trimmed;

            case WorkItemLinkKind.Url:
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    throw new ForgeException(ErrorKind.InvalidInput,
                        $"\"{trimmed}\" is not a web address.",
                        "Enter a full address starting with https://.");
                }

                return trimmed;

            default:
                return trimmed;
        }
    }

    /// <summary>File paths are case-insensitive on Windows; everything else is compared exactly.</summary>
    public static StringComparer LinkValueComparer(WorkItemLinkKind kind) =>
        kind == WorkItemLinkKind.File ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string? NormalizeLinkLabel(string? label)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxLinkLabelLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                "The link label is too long.",
                $"Keep it under {MaxLinkLabelLength} characters.");
        }

        return trimmed;
    }

    /// <summary>"#12" or "12" → 12, otherwise null.</summary>
    public static int? TryParseKey(string query)
    {
        var match = IssueNumber().Match(query.Trim());
        return match.Success && int.TryParse(match.Groups["n"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    private static string CollapseWhitespace(string text) => Whitespace().Replace(text.Trim(), " ");

    [GeneratedRegex(@"^[0-9a-fA-F]{4,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitHash();

    [GeneratedRegex(@"^#?(?<n>[0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueNumber();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
