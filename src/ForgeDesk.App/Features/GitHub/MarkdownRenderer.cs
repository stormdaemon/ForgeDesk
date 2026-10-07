using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>
/// Turns GitHub-flavored Markdown (Markdig) into a themed <see cref="FlowDocument"/>: headings,
/// paragraphs, emphasis, strikethrough, inline and fenced code, links, lists and task lists, quotes,
/// tables and rules. Every color is a resource reference so light and dark themes both work. HTML
/// is not interpreted: comments are dropped and other tags are stripped to their text.
/// </summary>
internal static partial class MarkdownRenderer
{
    /// <summary>Bodies larger than this are cut (GitHub allows 65 536 characters; logs pasted in issues can be huge).</summary>
    public const int MaxLength = 100_000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public static FlowDocument Render(string? markdown, string? emptyText)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = 13,
            TextAlignment = TextAlignment.Left,
            IsOptimalParagraphEnabled = false,
            IsHyphenationEnabled = false,
        };
        document.SetResourceReference(FlowDocument.FontFamilyProperty, "ForgeUIFont");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");

        if (string.IsNullOrWhiteSpace(markdown))
        {
            if (!string.IsNullOrEmpty(emptyText))
            {
                var empty = new Paragraph(new Run(emptyText)) { Margin = new Thickness(0), FontStyle = FontStyles.Italic };
                empty.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                document.Blocks.Add(empty);
            }

            return document;
        }

        var text = markdown.Length > MaxLength ? markdown[..MaxLength] + "\n\n…" : markdown;
        try
        {
            var parsed = Markdown.Parse(text, Pipeline);
            AddBlocks(document.Blocks, parsed);
        }
        catch (Exception ex)
        {
            // Never lose the content because of a rendering problem: show it as plain text.
            System.Diagnostics.Trace.TraceWarning($"Markdown rendering failed: {ex.Message}");
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
        }

        if (document.Blocks.FirstBlock is { } first)
        {
            first.Margin = new Thickness(0, 0, first.Margin.Right, first.Margin.Bottom);
        }

        if (document.Blocks.LastBlock is { } last)
        {
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        return document;
    }

    private static void AddBlocks(BlockCollection target, ContainerBlock container)
    {
        foreach (var block in container)
        {
            if (Convert(block) is { } converted)
            {
                target.Add(converted);
            }
        }
    }

    private static WpfBlock? Convert(Markdig.Syntax.Block block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Paragraph(paragraph),
        ListBlock list => List(list),
        QuoteBlock quote => Quote(quote),
        Markdig.Extensions.Tables.Table table => Table(table),
        FencedCodeBlock fenced => Code(fenced),
        CodeBlock code and not FencedCodeBlock => Code(code),
        ThematicBreakBlock => Rule(),
        HtmlBlock html => Html(html),
        LinkReferenceDefinitionGroup or LinkReferenceDefinition or BlankLineBlock => null,
        ContainerBlock other => Section(other),
        LeafBlock leaf when leaf.Inline is not null => Paragraph(leaf.Inline),
        _ => null,
    };

    private static Paragraph Heading(HeadingBlock heading)
    {
        var paragraph = new Paragraph
        {
            FontSize = heading.Level switch
            {
                1 => 20,
                2 => 17,
                3 => 15,
                _ => 13,
            },
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, heading.Level <= 2 ? 16 : 12, 0, heading.Level <= 2 ? 8 : 4),
        };
        if (heading.Level <= 2)
        {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 4);
            paragraph.SetResourceReference(WpfBlock.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        }

        if (heading.Level >= 4)
        {
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        }

        AddInlines(paragraph.Inlines, heading.Inline);
        return paragraph;
    }

    private static Paragraph Paragraph(ParagraphBlock block) => Paragraph(block.Inline);

    private static Paragraph Paragraph(ContainerInline? inline)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 19 };
        AddInlines(paragraph.Inlines, inline);
        return paragraph;
    }

    private static List List(ListBlock block)
    {
        var list = new List
        {
            MarkerStyle = block.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(20, 0, 0, 0),
        };
        if (block.IsOrdered && int.TryParse(block.OrderedStart, out var start) && start > 1)
        {
            list.StartIndex = start;
        }

        var isTaskList = block.OfType<ListItemBlock>().Any(item => item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList });
        if (isTaskList)
        {
            list.MarkerStyle = TextMarkerStyle.None;
            list.Padding = new Thickness(4, 0, 0, 0);
        }

        foreach (var item in block.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            foreach (var child in item)
            {
                if (Convert(child) is { } converted)
                {
                    // Tight lists: no gap between the paragraphs of items.
                    if (converted is System.Windows.Documents.Paragraph p)
                    {
                        p.Margin = new Thickness(0, 0, 0, 2);
                    }
                    else if (converted is List nested)
                    {
                        nested.Margin = new Thickness(0, 2, 0, 2);
                    }

                    listItem.Blocks.Add(converted);
                }
            }

            list.ListItems.Add(listItem);
        }

        return list;
    }

    private static Section Quote(QuoteBlock quote)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 0, 0, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
        section.SetResourceReference(WpfBlock.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        AddBlocks(section.Blocks, quote);
        if (section.Blocks.LastBlock is { } last)
        {
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        return section;
    }

    private static Section Section(ContainerBlock container)
    {
        var section = new Section();
        AddBlocks(section.Blocks, container);
        return section;
    }

    private static Paragraph Code(LeafBlock block)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 12,
            LineHeight = 17,
        };
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "ForgeMonoFont");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        var lines = block.Lines.Lines;
        var count = block.Lines.Count;

        // Drop trailing blank lines of indented code blocks.
        while (count > 0 && lines[count - 1].Slice.IsEmptyOrWhitespace())
        {
            count--;
        }

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[i].Slice.ToString()));
        }

        return paragraph;
    }

    private static Paragraph Rule()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 12), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 1 };
        paragraph.SetResourceReference(WpfBlock.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        return paragraph;
    }

    private static Paragraph? Html(HtmlBlock html)
    {
        if (html.Type == HtmlBlockType.Comment)
        {
            return null;
        }

        var text = StripTags(html.Lines.ToString()).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var paragraph = new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 8) };
        return paragraph;
    }

    private static WpfTable Table(Markdig.Extensions.Tables.Table table)
    {
        var result = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1, 1, 0, 0) };
        result.SetResourceReference(WpfBlock.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var columns = table.ColumnDefinitions.Count;
        foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            columns = Math.Max(columns, row.Count);
        }

        for (var i = 0; i < columns; i++)
        {
            result.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var tableRow = new WpfTableRow();
            if (row.IsHeader)
            {
                tableRow.FontWeight = FontWeights.SemiBold;
                tableRow.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            }

            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var tableCell = new WpfTableCell { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                tableCell.SetResourceReference(WpfTableCell.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                AddBlocks(tableCell.Blocks, cell);
                foreach (var block in tableCell.Blocks)
                {
                    block.Margin = new Thickness(0);
                }

                tableRow.Cells.Add(tableCell);
            }

            group.Rows.Add(tableRow);
        }

        result.RowGroups.Add(group);
        return result;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            if (Convert(inline) is { } converted)
            {
                target.Add(converted);
            }
        }
    }

    private static WpfInline? Convert(Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case TaskList task:
                return new Run(task.Checked ? "☑ " : "☐ ");
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontSize = 12 };
                run.SetResourceReference(TextElement.FontFamilyProperty, "ForgeMonoFont");
                run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                return run;
            }

            case EmphasisInline emphasis:
            {
                Span span = emphasis.DelimiterChar switch
                {
                    '~' => new Span { TextDecorations = TextDecorations.Strikethrough },
                    _ when emphasis.DelimiterCount >= 2 => new Bold(),
                    _ => new Italic(),
                };
                AddInlines(span.Inlines, emphasis);
                return span;
            }

            case LinkInline link:
                return Link(link);
            case AutolinkInline autolink:
            {
                var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
                return Hyperlink(url, new Run(autolink.Url));
            }

            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case HtmlInline html:
                return BreakTag().IsMatch(html.Tag) ? new LineBreak() : null;
            case ContainerInline container:
            {
                var span = new Span();
                AddInlines(span.Inlines, container);
                return span;
            }

            default:
                return null;
        }
    }

    private static WpfInline Link(LinkInline link)
    {
        var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;
        if (link.IsImage)
        {
            // Images are not downloaded: show their description as a link to them.
            var alt = new StringBuilder();
            foreach (var child in link)
            {
                if (child is LiteralInline literal)
                {
                    alt.Append(literal.Content.ToString());
                }
            }

            var label = alt.Length > 0 ? $"\U0001F5BC {alt}" : "\U0001F5BC image";
            return Hyperlink(url, new Run(label));
        }

        var content = new Span();
        AddInlines(content.Inlines, link);
        if (content.Inlines.Count == 0)
        {
            content.Inlines.Add(new Run(url));
        }

        return Hyperlink(url, content, link.Title);
    }

    private static WpfInline Hyperlink(string url, WpfInline content, string? title = null)
    {
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
        {
            return content;
        }

        var hyperlink = new Hyperlink(content) { NavigateUri = uri, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "ForgeInfoBrush");
        hyperlink.ToolTip = string.IsNullOrWhiteSpace(title) ? url : $"{title}\n{url}";
        return hyperlink;
    }

    private static string StripTags(string html) => WebUtilityDecode(TagPattern().Replace(CommentPattern().Replace(html, string.Empty), string.Empty));

    private static string WebUtilityDecode(string text) => System.Net.WebUtility.HtmlDecode(text);

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();
}
