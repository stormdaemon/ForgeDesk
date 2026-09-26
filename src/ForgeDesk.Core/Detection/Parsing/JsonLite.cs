using System.Text.Json;

namespace ForgeDesk.Core.Detection.Parsing;

/// <summary>Tolerant JSON helpers for manifests (comments and trailing commas allowed, as in tsconfig or tasks.json).</summary>
internal static class JsonLite
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>Parses a document; returns null and an explanation when the text is not valid JSON.</summary>
    public static JsonDocument? TryParse(string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            error = null;
            return JsonDocument.Parse(text.TrimStart('﻿'), Options);
        }
        catch (JsonException ex)
        {
            error = ex.LineNumber is { } line ? $"invalid JSON near line {line + 1}" : "invalid JSON";
            return null;
        }
    }

    public static string? GetString(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonElement? GetObject(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static JsonElement? GetArray(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    /// <summary>Names of an object's properties (empty when the property is missing or not an object).</summary>
    public static IEnumerable<string> PropertyNames(this JsonElement element, string property) =>
        element.GetObject(property) is { } obj ? obj.EnumerateObject().Select(p => p.Name).ToList() : [];

    /// <summary>String values of an object property whose values are strings ({"build": "tsc"}).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> StringMap(this JsonElement element, string property)
    {
        if (element.GetObject(property) is not { } obj)
        {
            return [];
        }

        return obj.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString() ?? string.Empty))
            .ToList();
    }

    public static IReadOnlyList<string> StringItems(this JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList()
            : [];
}
