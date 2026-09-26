namespace ForgeDesk.Presentation.Workspace;

/// <summary>Validation of new branch names (the rules of <c>git check-ref-format --branch</c>), with readable messages.</summary>
public static class BranchNames
{
    private const string ForbiddenCharacters = "~^:?*[\\";

    /// <summary>Returns an error message, or null when <paramref name="name"/> is a valid new branch name.</summary>
    public static string? Validate(string? name, IEnumerable<string>? existingBranches = null)
    {
        var candidate = name?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return "Enter a branch name.";
        }

        if (candidate.Any(char.IsWhiteSpace))
        {
            return "Branch names can't contain spaces. Use '-' or '/' instead.";
        }

        if (candidate.Any(c => char.IsControl(c) || ForbiddenCharacters.Contains(c, StringComparison.Ordinal)))
        {
            return "Branch names can't contain ~ ^ : ? * [ or \\.";
        }

        if (candidate.StartsWith('-'))
        {
            return "Branch names can't start with '-'.";
        }

        if (candidate == "@" || candidate.Contains("@{", StringComparison.Ordinal))
        {
            return "Branch names can't be '@' or contain '@{'.";
        }

        if (candidate.Contains("..", StringComparison.Ordinal))
        {
            return "Branch names can't contain '..'.";
        }

        if (candidate.StartsWith('/') || candidate.EndsWith('/') || candidate.Contains("//", StringComparison.Ordinal))
        {
            return "Branch names can't start or end with '/' or contain '//'.";
        }

        if (candidate.EndsWith('.') || candidate.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return "Branch names can't end with '.' or '.lock'.";
        }

        if (candidate.Split('/').Any(part => part.StartsWith('.')))
        {
            return "No part of a branch name can start with '.'.";
        }

        // Refs are files on Windows and macOS, so names differing only by case collide.
        if (existingBranches?.Any(b => string.Equals(b, candidate, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return $"A branch named '{candidate}' already exists.";
        }

        return null;
    }
}
