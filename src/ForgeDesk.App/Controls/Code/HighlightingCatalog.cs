using System.IO;
using ICSharpCode.AvalonEdit.Highlighting;

namespace ForgeDesk.App.Controls.Code;

/// <summary>
/// Resolves the highlighting definition for a file and caches the dark-adapted variants.
/// UI thread only (AvalonEdit highlighting objects are not thread-safe).
/// </summary>
internal static class HighlightingCatalog
{
    private static readonly Dictionary<string, IHighlightingDefinition> DarkVariants = new(StringComparer.Ordinal);

    public static IHighlightingDefinition? For(string? path, bool dark)
    {
        var definition = Find(path);
        if (definition is null || !dark)
        {
            return definition;
        }

        if (!DarkVariants.TryGetValue(definition.Name, out var variant))
        {
            variant = new DarkHighlightingDefinition(definition);
            DarkVariants[definition.Name] = variant;
        }

        return variant;
    }

    private static IHighlightingDefinition? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var manager = HighlightingManager.Instance;
        if (SyntaxLanguageMap.DefinitionNameFor(path) is { } name && manager.GetDefinition(name) is { } byName)
        {
            return byName;
        }

        var extension = Path.GetExtension(path);
        return extension.Length > 0 ? manager.GetDefinitionByExtension(extension) : null;
    }
}
