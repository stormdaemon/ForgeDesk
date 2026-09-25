using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ForgeDesk.App.Controls.Code;
using ForgeDesk.App.Theming;
using ICSharpCode.AvalonEdit.Search;

namespace ForgeDesk.App.Controls;

/// <summary>
/// Read-only source viewer (AvalonEdit): line numbers, the code font, syntax highlighting chosen
/// from <see cref="FilePath"/>'s extension with colors adapted to the dark theme, a find bar
/// (Ctrl+F, F3) and <see cref="ScrollToLine"/> to reveal and highlight a line.
/// </summary>
public partial class CodeView : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CodeView), new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(CodeView), new PropertyMetadata(null, OnFilePathChanged));

    public static readonly DependencyProperty ScrollToLineProperty = DependencyProperty.Register(
        nameof(ScrollToLine), typeof(int?), typeof(CodeView), new PropertyMetadata(null, OnScrollToLineChanged));

    public static readonly DependencyProperty WordWrapProperty = DependencyProperty.Register(
        nameof(WordWrap), typeof(bool), typeof(CodeView), new PropertyMetadata(false, OnWordWrapChanged));

    public static readonly DependencyProperty ShowLineNumbersProperty = DependencyProperty.Register(
        nameof(ShowLineNumbers), typeof(bool), typeof(CodeView), new PropertyMetadata(true, OnShowLineNumbersChanged));

    private static readonly Color FallbackAccentSubtle = Color.FromArgb(0x26, 0xF2, 0x76, 0x2E);
    private static readonly Color FallbackWarningSubtle = Color.FromArgb(0x40, 0xD2, 0x99, 0x22);

    private readonly SearchPanel _searchPanel;

    public CodeView()
    {
        InitializeComponent();

        var options = Editor.Options;
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;
        options.EnableTextDragDrop = false;
        options.AllowScrollBelowDocument = false;
        options.HighlightCurrentLine = false;
        options.ShowBoxForControlCharacters = true;
        Editor.TextArea.SelectionCornerRadius = 2;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.SelectionForeground = null;

        _searchPanel = SearchPanel.Install(Editor);
        _searchPanel.Style = (Style)FindResource("ForgeSearchPanelStyle");

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyTheme();
    }

    /// <summary>The content to display (the view is read-only).</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Path (or just the name) of the file, used to pick the syntax highlighting.</summary>
    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    /// <summary>1-based line to scroll into view and highlight, or null.</summary>
    public int? ScrollToLine
    {
        get => (int?)GetValue(ScrollToLineProperty);
        set => SetValue(ScrollToLineProperty, value);
    }

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public bool ShowLineNumbers
    {
        get => (bool)GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CodeView)d;
        view.Editor.Document.Text = e.NewValue as string ?? string.Empty;
        view.Editor.Document.UndoStack.ClearAll();
        view.Editor.ScrollToHome();
        view.ScheduleScrollToLine();
    }

    private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CodeView)d).ApplyHighlighting();

    private static void OnScrollToLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CodeView)d).ScheduleScrollToLine();

    private static void OnWordWrapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeView)d).Editor.WordWrap = (bool)e.NewValue;

    private static void OnShowLineNumbersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodeView)d).Editor.ShowLineNumbers = (bool)e.NewValue;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeProbe.Changed += OnThemeChanged;
        ApplyTheme();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ThemeProbe.Changed -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        // AvalonEdit freezes some brushes it is given, so it gets its own copies instead of the
        // shared theme brushes (which are recolored in place when the theme changes).
        var selection = ThemeProbe.FrozenBrush("ForgeAccentSubtleColor", FallbackAccentSubtle);
        Editor.TextArea.SelectionBrush = selection;
        Editor.TextArea.TextView.CurrentLineBackground = selection;
        Editor.TextArea.TextView.CurrentLineBorder = null;
        _searchPanel.MarkerBrush = ThemeProbe.FrozenBrush("ForgeWarningSubtleColor", FallbackWarningSubtle);
        ApplyHighlighting();
    }

    private void ApplyHighlighting() => Editor.SyntaxHighlighting = HighlightingCatalog.For(FilePath, ThemeProbe.IsDark);

    private void ScheduleScrollToLine()
    {
        if (ScrollToLine is not { } line)
        {
            Editor.Options.HighlightCurrentLine = false;
            return;
        }

        // Wait for the document to be laid out, otherwise the viewport height is unknown.
        Dispatcher.InvokeAsync(() => RevealLine(line), DispatcherPriority.Loaded);
    }

    private void RevealLine(int line)
    {
        if (line < 1 || line > Editor.Document.LineCount || ScrollToLine != line)
        {
            return;
        }

        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = 1;
        Editor.Options.HighlightCurrentLine = true;
        Editor.ScrollToLine(line);
    }
}
