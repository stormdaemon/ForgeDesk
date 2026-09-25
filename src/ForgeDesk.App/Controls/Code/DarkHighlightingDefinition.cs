using System.Windows.Media;
using ForgeDesk.App.Theming;
using ICSharpCode.AvalonEdit.Highlighting;

namespace ForgeDesk.App.Controls.Code;

/// <summary>
/// A copy of an AvalonEdit highlighting definition with its colors adapted for dark backgrounds.
/// AvalonEdit's built-in palettes (dark blue keywords, dark green comments…) are designed for
/// white editors; this keeps every rule and hue but lifts lightness to a readable contrast.
/// </summary>
internal sealed class DarkHighlightingDefinition : IHighlightingDefinition
{
    // Card surface on Mica in dark mode, used as the contrast reference.
    private static readonly Argb ReferenceBackground = Argb.FromUInt32(0xFF2B2B2B);

    private readonly IHighlightingDefinition _source;
    private readonly Dictionary<HighlightingRuleSet, HighlightingRuleSet> _ruleSets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<HighlightingColor, HighlightingColor> _colors = new(ReferenceEqualityComparer.Instance);

    public DarkHighlightingDefinition(IHighlightingDefinition source)
    {
        _source = source;
        MainRuleSet = Adapt(source.MainRuleSet);
        NamedHighlightingColors = source.NamedHighlightingColors.Select(Adapt).ToList();
    }

    public string Name => _source.Name;

    public HighlightingRuleSet MainRuleSet { get; }

    public IEnumerable<HighlightingColor> NamedHighlightingColors { get; }

    public IDictionary<string, string> Properties => _source.Properties;

    public HighlightingRuleSet GetNamedRuleSet(string name)
    {
        var ruleSet = _source.GetNamedRuleSet(name);
        return ruleSet is null ? null! : Adapt(ruleSet);
    }

    public HighlightingColor GetNamedColor(string name)
    {
        var color = _source.GetNamedColor(name);
        return color is null ? null! : Adapt(color);
    }

    public override string ToString() => Name;

    private HighlightingRuleSet Adapt(HighlightingRuleSet source)
    {
        if (_ruleSets.TryGetValue(source, out var existing))
        {
            return existing;
        }

        // Register before recursing: rule sets reference each other (and themselves) through spans.
        var copy = new HighlightingRuleSet { Name = source.Name };
        _ruleSets[source] = copy;

        foreach (var span in source.Spans)
        {
            copy.Spans.Add(new HighlightingSpan
            {
                StartExpression = span.StartExpression,
                EndExpression = span.EndExpression,
                RuleSet = span.RuleSet is null ? null : Adapt(span.RuleSet),
                StartColor = AdaptOrNull(span.StartColor),
                SpanColor = AdaptOrNull(span.SpanColor),
                EndColor = AdaptOrNull(span.EndColor),
                SpanColorIncludesStart = span.SpanColorIncludesStart,
                SpanColorIncludesEnd = span.SpanColorIncludesEnd,
            });
        }

        foreach (var rule in source.Rules)
        {
            copy.Rules.Add(new HighlightingRule { Regex = rule.Regex, Color = AdaptOrNull(rule.Color) });
        }

        return copy;
    }

    private HighlightingColor? AdaptOrNull(HighlightingColor? color) => color is null ? null : Adapt(color);

    private HighlightingColor Adapt(HighlightingColor source)
    {
        if (_colors.TryGetValue(source, out var existing))
        {
            return existing;
        }

        var copy = source.Clone();
        if (source.Foreground?.GetColor(null!) is { } foreground)
        {
            var adapted = ColorMath.AdaptForegroundForDarkBackground(ToArgb(foreground), ReferenceBackground);
            copy.Foreground = new SimpleHighlightingBrush(ToColor(adapted));
        }

        if (source.Background?.GetColor(null!) is { } background)
        {
            copy.Background = new SimpleHighlightingBrush(ToColor(ColorMath.AdaptBackgroundForDarkBackground(ToArgb(background))));
        }

        copy.Freeze();
        _colors[source] = copy;
        return copy;
    }

    private static Argb ToArgb(Color color) => new(color.A, color.R, color.G, color.B);

    private static Color ToColor(Argb color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
