using System.Buffers;
using System.Text.RegularExpressions;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Presentation.Dashboard;

/// <summary>Where clones go and what a valid repository address and folder name look like.</summary>
public static partial class CloneDestination
{
    // Windows rules, applied on every OS so the checks match what the app will meet.
    private static readonly SearchValues<char> InvalidNameCharacters = SearchValues.Create("\\/:*?\"<>|");

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>%USERPROFILE%\source\repos — Visual Studio's default, used until the user picks another folder.</summary>
    public static string DefaultBaseFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
        {
            profile = Path.GetTempPath();
        }

        return Path.Combine(profile, "source", "repos");
    }

    /// <summary>The configured clone folder, or the default one.</summary>
    public static string BaseFolderFrom(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultBaseFolder() : configured.Trim();

    /// <summary>Folder git would create for this address ("https://github.com/o/My.Repo.git" → "My.Repo").</summary>
    public static string SuggestedFolderName(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/', '\\');
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var name = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }

    /// <summary>Null when <paramref name="name"/> can be a folder name, else why not.</summary>
    public static string? ValidateFolderName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "Enter a name for the new folder.";
        }

        if (trimmed.AsSpan().IndexOfAny(InvalidNameCharacters) >= 0 || trimmed.Any(char.IsControl))
        {
            return "A folder name can't contain \\ / : * ? \" < > |";
        }

        if (trimmed is "." or ".." || trimmed.EndsWith('.'))
        {
            return "A folder name can't end with a period.";
        }

        if (ReservedNames.Contains(trimmed.Split('.')[0]))
        {
            return $"“{trimmed}” is reserved by Windows. Choose another name.";
        }

        return trimmed.Length > 200 ? "Use a shorter folder name." : null;
    }

    /// <summary>
    /// Checks the full destination: the base folder must be an absolute path and the target either
    /// missing or an empty folder. Returns null when the clone can go there.
    /// </summary>
    public static string? ValidateTarget(string? baseFolder, string? folderName)
    {
        if (ValidateFolderName(folderName) is { } nameError)
        {
            return nameError;
        }

        if (string.IsNullOrWhiteSpace(baseFolder) || !Path.IsPathFullyQualified(baseFolder.Trim()))
        {
            return "Choose the folder where the clone goes.";
        }

        string target;
        try
        {
            target = Path.Combine(baseFolder.Trim(), folderName!.Trim());
        }
        catch (ArgumentException)
        {
            return "This location is not a valid path.";
        }

        try
        {
            if (File.Exists(target))
            {
                return $"A file named “{folderName!.Trim()}” already exists there. Choose another name.";
            }

            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                return $"The folder “{folderName!.Trim()}” already exists there and isn't empty. Choose another name.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "ForgeDesk can't read this location. Choose a folder you have access to.";
        }

        return null;
    }

    /// <summary>
    /// Null when <paramref name="url"/> looks like something git can clone: an http(s), ssh or git
    /// URL, an scp-like address (git@github.com:owner/repo.git) or the path of a local repository.
    /// </summary>
    public static string? ValidateUrl(string? url)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "Enter the address of the repository.";
        }

        // Options ("-…") and remote helpers ("ext::…") could run arbitrary commands.
        if (text.StartsWith('-') || text.Contains("::", StringComparison.Ordinal) || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "This isn't a repository address.";
        }

        if (Path.IsPathFullyQualified(text))
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" or "git" or "file"
            && (uri.IsFile || (uri.Host.Length > 0 && uri.AbsolutePath.Trim('/').Length > 0)))
        {
            return null;
        }

        return ScpLikeAddress().IsMatch(text)
            ? null
            : "Use an HTTPS address (https://github.com/owner/repo.git), an SSH address (git@github.com:owner/repo.git) or a local path.";
    }

    /// <summary>The GitHub repository an address points to, or null for other hosts.</summary>
    public static GitHubRepoRef? ParseGitHub(string? url) => GitHubRemoteParser.Parse(url);

    [GeneratedRegex(@"^(?:[A-Za-z0-9._~-]+@)?[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])+:(?!/)[A-Za-z0-9._~/-]\S*$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLikeAddress();
}
