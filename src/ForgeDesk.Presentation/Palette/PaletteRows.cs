using CommunityToolkit.Mvvm.ComponentModel;

namespace ForgeDesk.Presentation.Palette;

/// <summary>A run of title text, emphasized when it matched the query.</summary>
public sealed record HighlightSegment(string Text, bool IsMatch);

/// <summary>A row of the palette result list: a group header or a result.</summary>
public abstract class PaletteRowViewModel : ObservableObject
{
    public abstract bool IsHeader { get; }
}

/// <summary>"Recent", "Projects", "Actions"… — not selectable.</summary>
public sealed class PaletteGroupHeaderViewModel : PaletteRowViewModel
{
    public PaletteGroupHeaderViewModel(string title) => Title = title;

    public string Title { get; }

    public override bool IsHeader => true;
}

public sealed partial class PaletteResultViewModel : PaletteRowViewModel
{
    public PaletteResultViewModel(PaletteItem item, IReadOnlyList<int> matchedIndices)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(matchedIndices);
        Item = item;
        TitleSegments = Segment(item.Title, matchedIndices);
    }

    public PaletteItem Item { get; }

    public string Title => Item.Title;

    public string? Subtitle => Item.Subtitle;

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon => Item.Icon;

    public string? Shortcut => Item.Shortcut;

    public PaletteCategory Category => Item.Category;

    public string CategoryTitle => PaletteQueryParser.CategoryTitle(Item.Category);

    public IReadOnlyList<HighlightSegment> TitleSegments { get; }

    public override bool IsHeader => false;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Splits <paramref name="text"/> into alternating matched / unmatched runs.</summary>
    public static IReadOnlyList<HighlightSegment> Segment(string text, IReadOnlyList<int> matchedIndices)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (matchedIndices is null || matchedIndices.Count == 0 || text.Length == 0)
        {
            return [new HighlightSegment(text, false)];
        }

        var matched = new bool[text.Length];
        foreach (var index in matchedIndices)
        {
            if (index >= 0 && index < text.Length)
            {
                matched[index] = true;
            }
        }

        var segments = new List<HighlightSegment>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || matched[i] != matched[start])
            {
                segments.Add(new HighlightSegment(text[start..i], matched[start]));
                start = i;
            }
        }

        return segments;
    }
}
