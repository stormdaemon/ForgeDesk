using System.Globalization;
using ForgeDesk.Core.GitHub;

namespace ForgeDesk.Presentation.Dashboard;

/// <summary>A repository of the signed-in GitHub account, in the clone dialog.</summary>
public sealed class RemoteRepositoryItemViewModel
{
    public RemoteRepositoryItemViewModel(GitHubRepository repository, bool isAlreadyAdded = false)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        IsAlreadyAdded = isAlreadyAdded;
    }

    public GitHubRepository Repository { get; }

    /// <summary>A project of ForgeDesk already points to this repository.</summary>
    public bool IsAlreadyAdded { get; }

    public string Owner => Repository.Owner;

    public string Name => Repository.Name;

    public string FullName => Repository.FullName;

    public string? Description => string.IsNullOrWhiteSpace(Repository.Description) ? null : Repository.Description.Trim();

    public bool IsPrivate => Repository.IsPrivate;

    public bool IsFork => Repository.IsFork;

    public bool IsArchived => Repository.IsArchived;

    public string? Language => string.IsNullOrWhiteSpace(Repository.Language) ? null : Repository.Language;

    public int Stars => Repository.Stars;

    public bool HasStars => Stars > 0;

    /// <summary>"12", "1.2k", "34k".</summary>
    public string StarsText => Stars switch
    {
        < 1000 => Stars.ToString(CultureInfo.CurrentCulture),
        < 10_000 => (Stars / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + "k",
        _ => (Stars / 1000).ToString(CultureInfo.CurrentCulture) + "k",
    };

    /// <summary>Last push (or metadata update) — what "Updated 3 days ago" means on GitHub.</summary>
    public DateTimeOffset? UpdatedAt => Repository.PushedAt ?? Repository.UpdatedAt;

    public string CloneUrl => Repository.CloneUrl;

    public string ToolTip => Description is { } description ? $"{FullName}\n{description}" : FullName;

    public bool Matches(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return terms.All(term =>
            FullName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (Description?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (Language?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }
}
