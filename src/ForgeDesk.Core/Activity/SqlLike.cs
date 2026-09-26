namespace ForgeDesk.Core.Activity;

/// <summary>Builds SQLite LIKE patterns that match user text literally (used with <c>ESCAPE '\'</c>).</summary>
internal static class SqlLike
{
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    /// <summary>"%text%" with the wildcards of <paramref name="text"/> escaped.</summary>
    public static string Contains(string text) => "%" + Escape(text) + "%";
}
