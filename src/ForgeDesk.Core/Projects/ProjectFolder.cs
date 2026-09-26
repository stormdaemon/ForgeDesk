using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Projects;

/// <summary>Validation of folders chosen as project roots.</summary>
internal static class ProjectFolder
{
    /// <summary>
    /// Normalizes <paramref name="path"/> and checks it is an existing folder that can reasonably be a
    /// project: not a file, not the root of a drive, not the user profile folder itself.
    /// </summary>
    public static string Validate(string? path)
    {
        var normalized = NormalizeOrThrow(path);
        if (File.Exists(normalized))
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{normalized}' is a file, not a folder.",
                "Choose the folder that contains the project.");
        }

        if (!Directory.Exists(normalized))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{normalized}' does not exist.",
                "Check the path, or choose the folder again.");
        }

        EnsureNotTooBroad(normalized);
        return normalized;
    }

    public static string NormalizeOrThrow(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Choose a folder.");
        }

        try
        {
            return PathUtil.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{path.Trim()}' is not a valid folder path.",
                "Choose the folder again with the folder picker.", ex.Message, ex);
        }
    }

    /// <summary>Normalized path, or null when <paramref name="path"/> is not a valid path at all.</summary>
    public static string? TryNormalize(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : PathUtil.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static bool IsDriveRoot(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath);
        return !string.IsNullOrEmpty(root) && string.Equals(PathUtil.Normalize(root), normalizedPath, PathUtil.Comparison);
    }

    /// <summary>Scanning a whole drive or the whole user profile would be slow and meaningless.</summary>
    private static void EnsureNotTooBroad(string normalizedPath)
    {
        if (IsDriveRoot(normalizedPath))
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{normalizedPath}' is the root of a drive, which is too broad to be a project.",
                "Choose the folder of a specific project instead.");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && PathUtil.AreSame(profile, normalizedPath))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Your user folder is too broad to be a project.",
                "Choose the folder of a specific project inside it instead.");
        }
    }
}
