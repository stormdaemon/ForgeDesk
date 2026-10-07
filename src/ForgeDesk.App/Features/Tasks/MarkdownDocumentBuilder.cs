using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfList = System.Windows.Documents.List;

namespace ForgeDesk.App.Features.Tasks;

/// <summary>
/// Renders a task description written in Markdown (CommonMark + tables, task lists, autolinks)
/// as a themed <see cref="FlowDocument"/>. Brushes are looked up from <paramref name="resources"/>
/// so the preview follows the light and dark themes.
/// </summary>
public static class MarkdownDocumentBuilder
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    public static FlowDocument Build(string? markdown, FrameworkElement resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = resources.TryFindResource("ForgeUIFont") as FontFamily ?? new FontFamily("Segoe UI"),
            FontSize = 13,
            TextAlignment = TextAlignment.Left,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");

        var parsed = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var context = new RenderContext(resources);
        foreach (var block in parsed)
        {
            if (RenderBlock(block, context) is { } rendered)
            {
                document.Blocks.Add(rendered);
            }
        }

        return document;
    }

    private sealed class RenderContext(FrameworkElement resources)
    {
        public FontFamily MonoFont { get; } = resources.TryFindResource("ForgeMonoFont") as FontFamily ?? new FontFamily("Consolas");
    }

    private static WpfBlock? RenderBlock(MdBlock block, RenderContext context)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var paragraph = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = heading.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 13 },
                    Margin = new Thickness(0, heading.Level <= 2 ? 12 : 8, 0, 4),
                };
                AddInlines(paragraph.Inlines, heading.Inline, context);
                return paragraph;
            }

            case ParagraphBlock paragraphBlock:
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 20 };
                AddInlines(paragraph.Inlines, paragraphBlock.Inline, context);
                return paragraph;
            }

            case ListBlock listBlock:
            {
                var list = new WpfList
                {
                    MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(20, 0, 0, 0),
                };
                if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, out var start) && start > 1)
                {
                    list.StartIndex = start;
                }

                foreach (var child in listBlock)
                {
                    if (child is not ListItemBlock itemBlock)
                    {
                        continue;
                    }

                    var item = new ListItem();
                    foreach (var inner in itemBlock)
                    {
                        if (RenderBlock(inner, context) is { } rendered)
                        {
                            if (rendered is Paragraph p)
                            {
                                p.Margin = new Thickness(0, 0, 0, 2);
                            }

                            item.Blocks.Add(rendered);
                        }
                    }

                    list.ListItems.Add(item);
                }

                return list;
            }

            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 8),
                };
                section.SetResourceReference(Section.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                section.SetResourceReference(Section.ForegroundProperty, "TextFillColorSecondaryBrush");
                foreach (var inner in quote)
                {
                    if (RenderBlock(inner, context) is { } rendered)
                    {
                        section.Blocks.Add(rendered);
                    }
                }

                return section;
            }

            case CodeBlock code:
            {
                var paragraph = new Paragraph
                {
                    FontFamily = context.MonoFont,
                    FontSize = 12,
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                };
                paragraph.SetResourceReference(Paragraph.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                paragraph.Inlines.Add(new Run(code.Lines.ToString().TrimEnd('\r', '\n')));
                return paragraph;
            }

            case ThematicBreakBlock:
            {
                var rule = new Paragraph { Margin = new Thickness(0, 4, 0, 12), FontSize = 1, BorderThickness = new Thickness(0, 0, 0, 1) };
                rule.SetResourceReference(Paragraph.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                return rule;
            }

            case MdTable table:
                return RenderTable(table, context);

            case HtmlBlock html:
                return new Paragraph(new Run(html.Lines.ToString())) { FontFamily = context.MonoFont, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };

            case LeafBlock leaf when leaf.Inline is not null:
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(paragraph.Inlines, leaf.Inline, context);
                return paragraph;
            }

            default:
                return null;
        }
    }

    private static Table RenderTable(MdTable table, RenderContext context)
    {
        var result = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        var columns = table.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++)
        {
            result.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        foreach (var rowBlock in table.OfType<MdTableRow>())
        {
            var row = new TableRow();
            foreach (var cellBlock in rowBlock.OfType<MdTableCell>())
            {
                var cell = new TableCell { Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0, 0, 0, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                foreach (var inner in cellBlock)
                {
                    if (RenderBlock(inner, context) is { } rendered)
                    {
                        if (rendered is Paragraph p)
                        {
                            p.Margin = new Thickness(0);
                            if (rowBlock.IsHeader)
                            {
                                p.FontWeight = FontWeights.SemiBold;
                            }
                        }

                        cell.Blocks.Add(rendered);
                    }
                }

                row.Cells.Add(cell);
            }

            group.Rows.Add(row);
        }

        result.RowGroups.Add(group);
        return result;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container, RenderContext context)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            if (RenderInline(inline, context) is { } rendered)
            {
                target.Add(rendered);
            }
        }
    }

    private static WpfInline? RenderInline(MdInline inline, RenderContext context)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());

            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = context.MonoFont, FontSize = 12 };
                run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                return run;
            }

            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");

            case TaskList task:
                return new Run(task.Checked ? "☑ " : "☐ ");

            case EmphasisInline emphasis:
            {
                var span = new Span();
                AddInlines(span.Inlines, emphasis, context);
                if (emphasis.DelimiterChar == '~')
                {
                    span.TextDecorations = TextDecorations.Strikethrough;
                }
                else if (emphasis.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    span.FontStyle = FontStyles.Italic;
                }

                return span;
            }

            case LinkInline link when link.IsImage:
            {
                var alt = new Span();
                AddInlines(alt.Inlines, link, context);
                alt.Inlines.InsertBefore(alt.Inlines.FirstInline ?? (WpfInline)new Run(), new Run("🖼 "));
                return alt;
            }

            case LinkInline link:
            {
                var hyperlink = CreateHyperlink(link.Url);
                AddInlines(hyperlink.Inlines, link, context);
                if (hyperlink.Inlines.Count == 0)
                {
                    hyperlink.Inlines.Add(new Run(link.Url ?? string.Empty));
                }

                return hyperlink;
            }

            case AutolinkInline autolink:
            {
                var hyperlink = CreateHyperlink(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url);
                hyperlink.Inlines.Add(new Run(autolink.Url));
                return hyperlink;
            }

            case HtmlInline html:
                return new Run(html.Tag);

            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());

            case ContainerInline nested:
            {
                var span = new Span();
                AddInlines(span.Inlines, nested, context);
                return span;
            }

            default:
                return null;
        }
    }

    private static Hyperlink CreateHyperlink(string? url)
    {
        var hyperlink = new Hyperlink();
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "ForgeInfoBrush");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.ToolTip = uri.ToString();
            hyperlink.RequestNavigate += (_, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Trace.TraceWarning($"Could not open {e.Uri}: {ex.Message}");
                }

                e.Handled = true;
            };
        }

        return hyperlink;
    }
}

/// <summary>Attached property that shows Markdown in a read-only <see cref="RichTextBox"/>.</summary>
public static class MarkdownView
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(MarkdownView), new PropertyMetadata(null, OnMarkdownChanged));

    public static string? GetMarkdown(DependencyObject element) => (string?)element?.GetValue(MarkdownProperty);

    public static void SetMarkdown(DependencyObject element, string? value) => element?.SetValue(MarkdownProperty, value);

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox box)
        {
            box.Document = MarkdownDocumentBuilder.Build(e.NewValue as string, box);
        }
    }
}
