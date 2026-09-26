using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Git;

internal sealed partial class GitService
{
    public Task<GitInstallation?> FindGitAsync(CancellationToken cancellationToken = default) =>
        _cli.Locator.FindAsync(cancellationToken);

    public async Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default) =>
        await GetRepositoryRootAsync(path, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<string?> GetRepositoryRootAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = PathUtil.Normalize(path);
        if (File.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        if (!Directory.Exists(directory))
        {
            return null;
        }

        // --show-cdup ("../../") rather than --show-toplevel: the root keeps the form the caller used
        // (8.3 short names, subst drives, symlinks), so it compares equal to the paths ForgeDesk stores.
        var result = await ExecuteAsync(directory, ["rev-parse", "--is-inside-work-tree", "--show-cdup"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var error = GitErrorTranslator.Translate(result);
            return error.Kind == ErrorKind.NotARepository ? null : throw error;
        }

        var lines = result.StandardOutput.Split('\n');
        if (lines[0].Trim() != "true")
        {
            return null;
        }

        var up = lines.Length > 1 ? lines[1].Trim() : string.Empty;
        return PathUtil.Normalize(up.Length == 0 ? directory : Path.Combine(directory, PathUtil.ToPlatform(up)));
    }

    public async Task InitAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = RepositoryDirectory(path);
        Directory.CreateDirectory(directory);
        await RunAsync(directory, ["init", "--initial-branch=main"], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task CloneAsync(string url, string targetDirectory, IProgress<GitProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = GitArguments.Name(url, "repository address");
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var target = PathUtil.Normalize(targetDirectory);
        if (File.Exists(target))
        {
            throw new ForgeException(ErrorKind.AlreadyExists, $"A file named '{Path.GetFileName(target)}' already exists there.",
                "Choose another folder name for the repository.");
        }

        var existed = Directory.Exists(target);
        if (existed && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new ForgeException(ErrorKind.AlreadyExists, $"The folder '{target}' already exists and isn't empty.",
                "Choose an empty folder or a new folder name.");
        }

        var parent = Path.GetDirectoryName(target) ?? throw ForgeException.InvalidInput("Choose a folder for the repository, not a drive root.");
        Directory.CreateDirectory(parent);
        var request = new GitRequest
        {
            WorkingDirectory = parent,
            Arguments = ["clone", "--progress", "--", source, target],
            Kind = GitCommandKind.Clone,
            Environment = await CredentialEnvironmentAsync([source], cancellationToken).ConfigureAwait(false),
            Progress = progress,
        };

        try
        {
            await _cli.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Failed, timed out or cancelled: never leave a half-cloned repository behind.
            await WorkingTreeFiles.RemoveCloneLeftoversAsync(target, keepFolder: existed).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<GitIdentity> GetIdentityAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        var result = await ExecuteAsync(repository, ["config", "-z", "--get-regexp", @"^user\.(name|email)$"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            // No match: nothing configured at any level.
            return new GitIdentity(null, null);
        }

        if (!result.Succeeded)
        {
            throw GitErrorTranslator.Translate(result);
        }

        string? name = null, email = null;
        // "key\nvalue\0" per entry, from system to local scope: the last one wins, as in git.
        foreach (var entry in result.StandardOutput.Split('\0'))
        {
            var separator = entry.IndexOf('\n', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = entry[..separator].Trim();
            var value = entry[(separator + 1)..].TrimEnd('\n');
            if (key.Equals("user.name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("user.email", StringComparison.OrdinalIgnoreCase))
            {
                email = value;
            }
        }

        return new GitIdentity(string.IsNullOrWhiteSpace(name) ? null : name, string.IsNullOrWhiteSpace(email) ? null : email);
    }

    public async Task SetGlobalIdentityAsync(string name, string email, CancellationToken cancellationToken = default)
    {
        var trimmedName = name?.Trim();
        var trimmedEmail = email?.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Any(char.IsControl))
        {
            throw ForgeException.InvalidInput("Enter your name.");
        }

        if (string.IsNullOrEmpty(trimmedEmail) || trimmedEmail.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || !trimmedEmail.Contains('@', StringComparison.Ordinal))
        {
            throw ForgeException.InvalidInput("Enter a valid email address.");
        }

        var directory = NeutralDirectory();
        await RunAsync(directory, ["config", "--global", "user.name", trimmedName], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
        await RunAsync(directory, ["config", "--global", "user.email", trimmedEmail], cancellationToken, GitCommandKind.Write).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var repository = RepositoryDirectory(repoPath);
        // Line output (C-quoted names) rather than -z: the capture limit then truncates the list
        // gracefully on gigantic repositories instead of dropping one giant NUL-separated line.
        var result = await RunAsync(repository, ["ls-files", "--cached", "--others", "--exclude-standard", "--deduplicate", "--full-name"], cancellationToken)
            .ConfigureAwait(false);
        return GitRefParsers.ParsePathLines(result.StandardOutput);
    }

    /// <summary>A folder outside any repository, for commands about the user rather than a repository.</summary>
    private static string NeutralDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Directory.Exists(home) ? home : Path.GetTempPath();
    }
}
