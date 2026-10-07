namespace ForgeDesk.Presentation.Git;

/// <summary>Validation of new tag names (the rules of <c>git check-ref-format</c>), with readable messages.</summary>
public static class TagNames
{
    private const string ForbiddenCharacters = "~^:?*[\\";

    /// <summary>Returns an error message, or null when <paramref name="name"/> is a valid new tag name.</summary>
    public static string? Validate(string? name, IEnumerable<string>? existingTags = null)
    {
        var candidate = name?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return "Enter a tag name, for example v1.2.0.";
        }

        if (candidate.Any(char.IsWhiteSpace))
        {
            return "Tag names can't contain spaces.";
        }

        if (candidate.Any(c => char.IsControl(c) || ForbiddenCharacters.Contains(c, StringComparison.Ordinal)))
        {
            return "Tag names can't contain ~ ^ : ? * [ or \\.";
        }

        if (candidate.StartsWith('-'))
        {
            return "Tag names can't start with '-'.";
        }

        if (candidate == "@" || candidate.Contains("@{", StringComparison.Ordinal))
        {
            return "Tag names can't be '@' or contain '@{'.";
        }

        if (candidate.Contains("..", StringComparison.Ordinal))
        {
            return "Tag names can't contain '..'.";
        }

        if (candidate.StartsWith('/') || candidate.EndsWith('/') || candidate.Contains("//", StringComparison.Ordinal))
        {
            return "Tag names can't start or end with '/' or contain '//'.";
        }

        if (candidate.EndsWith('.') || candidate.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return "Tag names can't end with '.' or '.lock'.";
        }

        if (candidate.Split('/').Any(part => part.StartsWith('.')))
        {
            return "No part of a tag name can start with '.'.";
        }

        if (existingTags?.Any(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return $"A tag named '{candidate}' already exists.";
        }

        return null;
    }
}
