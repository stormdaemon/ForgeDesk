using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ForgeDesk.Presentation.Palette;

namespace ForgeDesk.App.Features.Palette;

/// <summary>
/// Renders <see cref="HighlightSegment"/>s into a TextBlock: matched characters in semibold accent,
/// the rest in the TextBlock's own style. <c>palette:HighlightedText.Segments="{Binding TitleSegments}"</c>.
/// </summary>
public static class HighlightedText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<HighlightSegment>), typeof(HighlightedText), new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<HighlightSegment>? GetSegments(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IReadOnlyList<HighlightSegment>?)element.GetValue(SegmentsProperty);
    }

    public static void SetSegments(DependencyObject element, IReadOnlyList<HighlightSegment>? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SegmentsProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        text.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<HighlightSegment> segments)
        {
            return;
        }

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.SetResourceReference(TextElement.ForegroundProperty, "ForgeAccentTextBrush");
            }

            text.Inlines.Add(run);
        }
    }
}
