using System.Text.RegularExpressions;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Projects;

/// <summary>Validation of repository addresses accepted by "Clone repository".</summary>
internal static partial class CloneUrl
{
    private static readonly string[] NetworkSchemes = ["https", "http", "ssh", "git"];

    /// <summary>
    /// Accepts https/http, ssh and git URLs, scp-like addresses (git@github.com:owner/repo.git),
    /// file URLs and absolute paths of local repositories. Rejects anything git could read as an
    /// option ("-…") or a remote-helper transport ("ext::…"), which could run arbitrary commands.
    /// </summary>
    public static string Validate(string? remoteUrl)
    {
        var url = remoteUrl?.Trim() ?? string.Empty;
        if (url.Length == 0)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Enter the address of the repository to clone.", Hint);
        }

        if (url.StartsWith('-') || url.Contains("::", StringComparison.Ordinal) || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw Invalid(url);
        }

        // "C:repo" is relative to the current folder of drive C: — ambiguous, never what the user meant.
        if (url.Length >= 2 && char.IsAsciiLetter(url[0]) && url[1] == ':' && !Path.IsPathFullyQualified(url))
        {
            throw Invalid(url);
        }

        if (Path.IsPathFullyQualified(url))
        {
            return Directory.Exists(url)
                ? url
                : throw new ForgeException(ErrorKind.PathNotFound, $"The repository folder '{url}' does not exist.", "Check the path of the local repository.");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return Directory.Exists(uri.LocalPath)
                    ? url
                    : throw new ForgeException(ErrorKind.PathNotFound, $"The repository folder '{uri.LocalPath}' does not exist.", "Check the path of the local repository.");
            }

            if (NetworkSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) && uri.Host.Length > 0 && uri.AbsolutePath.Trim('/').Length > 0)
            {
                return url;
            }

            // "git@github.com:owner/repo" also parses as a URI with the scheme "git@github.com": fall through.
        }

        var scp = ScpLikeAddress().Match(url);
        if (scp.Success && scp.Groups["host"].Value.Length > 1)
        {
            return url;
        }

        throw Invalid(url);
    }

    /// <summary>The address without credentials (https://user:token@host/… → https://host/…), for journals and messages.</summary>
    public static string Redact(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile && uri.UserInfo.Length > 0)
        {
            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
        }

        return url;
    }

    /// <summary>Folder name git would create for this address ("https://github.com/o/My.Repo.git" → "My.Repo").</summary>
    public static string SuggestedFolderName(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var trimmed = url.Trim().TrimEnd('/', '\\');
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var name = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length == 0 ? "repository" : name;
    }

    private const string Hint = "Use an HTTPS address (https://github.com/owner/repo.git), an SSH address (git@github.com:owner/repo.git) or the path of a local repository.";

    private static ForgeException Invalid(string url) =>
        new(ErrorKind.InvalidInput, $"'{Redact(url)}' is not a repository address ForgeDesk can clone.", Hint);

    // user@host:path — the host needs at least two characters so "C:repo" is not taken for one.
    [GeneratedRegex(@"^(?:[A-Za-z0-9._~-]+@)?(?<host>[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?):(?!/)(?<path>[A-Za-z0-9._~/-][^\s]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLikeAddress();
}
