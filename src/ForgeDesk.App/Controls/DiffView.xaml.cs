using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ForgeDesk.App.Controls.Diff;
using ForgeDesk.App.Services;
using ForgeDesk.App.Theming;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Controls;

/// <summary>
/// Unified diff of one file (<see cref="Diff"/>): old/new line numbers, +/- gutters, added and
/// removed backgrounds, hunk headers with an optional per-hunk action (<see cref="HunkActionText"/>
/// + <see cref="HunkActionCommand"/>, parameter = the DiffHunk), and binary / too large / empty
/// states. Rows have a fixed height in a recycling virtualized list, so 20 000-line diffs scroll
/// smoothly. Selected rows can be copied with Ctrl+C.
/// </summary>
public partial class DiffView : UserControl
{
    public static readonly DependencyProperty DiffProperty = DependencyProperty.Register(
        nameof(Diff), typeof(FileDiff), typeof(DiffView), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty HunkActionTextProperty = DependencyProperty.Register(
        nameof(HunkActionText), typeof(string), typeof(DiffView), new PropertyMetadata(null));

    public static readonly DependencyProperty HunkActionCommandProperty = DependencyProperty.Register(
        nameof(HunkActionCommand), typeof(ICommand), typeof(DiffView), new PropertyMetadata(null));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(DiffView), new PropertyMetadata("Select a file to see its changes.", OnContentChanged));

    private static readonly DependencyPropertyKey RowHeightPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(RowHeight), typeof(double), typeof(DiffView), new PropertyMetadata(20.0));

    private static readonly DependencyPropertyKey GutterWidthPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(GutterWidth), typeof(GridLength), typeof(DiffView), new PropertyMetadata(new GridLength(48)));

    private static readonly DependencyPropertyKey ContentWidthPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ContentWidth), typeof(double), typeof(DiffView), new PropertyMetadata(0.0));

    private static readonly DependencyPropertyKey ActionButtonHeightPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ActionButtonHeight), typeof(double), typeof(DiffView), new PropertyMetadata(18.0));

    public static readonly DependencyProperty RowHeightProperty = RowHeightPropertyKey.DependencyProperty;
    public static readonly DependencyProperty GutterWidthProperty = GutterWidthPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ContentWidthProperty = ContentWidthPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ActionButtonHeightProperty = ActionButtonHeightPropertyKey.DependencyProperty;

    private DiffRows? _rows;

    public DiffView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThemeProbe.Changed += OnThemeChanged;
            UpdateMetrics();
        };
        Unloaded += (_, _) => ThemeProbe.Changed -= OnThemeChanged;
        Render();
    }

    /// <summary>The diff to display; null shows <see cref="PlaceholderText"/>.</summary>
    public FileDiff? Diff
    {
        get => (FileDiff?)GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    /// <summary>Label of the button on each hunk header ("Stage hunk"); no button when null.</summary>
    public string? HunkActionText
    {
        get => (string?)GetValue(HunkActionTextProperty);
        set => SetValue(HunkActionTextProperty, value);
    }

    /// <summary>Executed with the <see cref="DiffHunk"/> as parameter.</summary>
    public ICommand? HunkActionCommand
    {
        get => (ICommand?)GetValue(HunkActionCommandProperty);
        set => SetValue(HunkActionCommandProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Row height derived from the code font size (read-only).</summary>
    public double RowHeight => (double)GetValue(RowHeightProperty);

    /// <summary>Width of each line-number column (read-only).</summary>
    public GridLength GutterWidth => (GridLength)GetValue(GutterWidthProperty);

    /// <summary>Width of the longest row, shared by all rows so horizontal scrolling is stable (read-only).</summary>
    public double ContentWidth => (double)GetValue(ContentWidthProperty);

    /// <summary>Height of hunk action buttons, fitted to the row (read-only).</summary>
    public double ActionButtonHeight => (double)GetValue(ActionButtonHeightProperty);

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DiffView)d).Render();

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateMetrics();

    private void Render()
    {
        var diff = Diff;
        _rows = null;
        if (diff is null)
        {
            ShowState(SymbolRegular.DocumentText24, PlaceholderText, null);
        }
        else if (diff.IsBinary)
        {
            ShowState(SymbolRegular.DocumentOnePage24, "Binary file", "ForgeDesk shows line changes for text files only.");
        }
        else if (diff.IsTooLarge)
        {
            ShowState(SymbolRegular.DocumentError24, "This diff is too large to display",
                "Open the file in your editor to review these changes.");
        }
        else if (diff.Hunks.Count == 0)
        {
            var (title, description) = diff switch
            {
                { IsNewFile: true } => ("Empty file", "This new file has no content yet."),
                { IsDeletedFile: true } => ("Empty file deleted", "The deleted file had no content."),
                { OldPath: { } oldPath } when !string.Equals(oldPath, diff.Path, StringComparison.Ordinal) =>
                    ("Renamed without changes", $"Moved from {oldPath}; the content is identical."),
                _ => ("No content changes", "Only file metadata, such as permissions, changed."),
            };
            ShowState(SymbolRegular.DocumentText24, title, description);
        }
        else
        {
            _rows = DiffRowBuilder.Build(diff);
            Lines.ItemsSource = _rows.Rows;
            UpdateMetrics();
            StateMessage.Visibility = Visibility.Collapsed;
            Lines.Visibility = Visibility.Visible;
            Lines.ScrollIntoView(_rows.Rows[0]);
        }
    }

    private void ShowState(SymbolRegular icon, string title, string? description)
    {
        Lines.ItemsSource = null;
        Lines.Visibility = Visibility.Collapsed;
        StateMessage.Icon = icon;
        StateMessage.Title = title;
        StateMessage.Description = description;
        StateMessage.Visibility = Visibility.Visible;
    }

    private void UpdateMetrics()
    {
        var fontFamily = TryFindResource("ForgeCodeFont") as FontFamily ?? new FontFamily("Consolas");
        var fontSize = TryFindResource("ForgeCodeFontSize") is double size && double.IsFinite(size) ? size : 13;
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var sample = new FormattedText("0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var charWidth = sample.WidthIncludingTrailingWhitespace / 10;

        var rowHeight = Math.Max(16, Math.Round(fontSize * 1.54));
        var digits = _rows?.LineNumberDigits ?? 3;
        var gutter = Math.Ceiling((digits * charWidth) + 16);
        var longest = _rows?.LongestLine ?? 0;

        SetValue(RowHeightPropertyKey, rowHeight);
        SetValue(ActionButtonHeightPropertyKey, Math.Max(18, rowHeight - 4));
        SetValue(GutterWidthPropertyKey, new GridLength(gutter));
        SetValue(ContentWidthPropertyKey, Math.Ceiling((gutter * 2) + 20 + (longest * charWidth) + 36));
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Lines.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, Lines.SelectedItems.OfType<DiffRow>()
            .Where(row => row.Kind != DiffRowKind.NoNewline)
            .OrderBy(row => row.Index)
            .Select(row => row.Text));
        try
        {
            ClipboardWriter.SetText(text);
        }
        catch (ForgeException)
        {
            // The clipboard stayed locked by another program; the user can simply copy again.
        }

        e.Handled = true;
    }
}
