using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ForgeDesk.App.Features.Commands;

/// <summary>
/// Shows <see cref="TextProperty"/> in a TextBlock with every occurrence of <see cref="QueryProperty"/>
/// highlighted (case-insensitive). Lines without a match keep the plain, fast Text path.
/// </summary>
public static class LogHighlighter
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LogHighlighter), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(LogHighlighter), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject element) => (string?)element?.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element?.SetValue(TextProperty, value);

    public static string? GetQuery(DependencyObject element) => (string?)element?.GetValue(QueryProperty);

    public static void SetQuery(DependencyObject element, string? value) => element?.SetValue(QueryProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var text = GetText(block) ?? string.Empty;
        var query = GetQuery(block);
        if (string.IsNullOrEmpty(query) || text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
        {
            block.Inlines.Clear();
            block.Text = text;
            return;
        }

        var highlight = block.TryFindResource("ForgeWarningSubtleBrush") as Brush ?? Brushes.Khaki;
        block.Inlines.Clear();
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                block.Inlines.Add(new Run(text[start..]));
                break;
            }

            if (index > start)
            {
                block.Inlines.Add(new Run(text[start..index]));
            }

            block.Inlines.Add(new Run(text.Substring(index, query.Length)) { Background = highlight, FontWeight = FontWeights.Bold });
            start = index + query.Length;
        }
    }
}
