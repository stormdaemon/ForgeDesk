using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace ForgeDesk.App.Features.GitHub;

/// <summary>
/// Renders GitHub-flavored Markdown (descriptions, comments, release notes) in a read-only, themed
/// <see cref="FlowDocumentScrollViewer"/>. Links run <see cref="LinkCommand"/> with their absolute URL
/// (relative links are resolved against <see cref="BaseUrl"/>). Reused by the Releases feature.
/// </summary>
public partial class MarkdownViewer : UserControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownViewer), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(MarkdownViewer), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty IsScrollableProperty = DependencyProperty.Register(
        nameof(IsScrollable), typeof(bool), typeof(MarkdownViewer), new PropertyMetadata(false, OnScrollableChanged));

    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand), typeof(ICommand), typeof(MarkdownViewer), new PropertyMetadata(null));

    public static readonly DependencyProperty BaseUrlProperty = DependencyProperty.Register(
        nameof(BaseUrl), typeof(string), typeof(MarkdownViewer), new PropertyMetadata(null));

    public MarkdownViewer()
    {
        InitializeComponent();
        Viewer.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnRequestNavigate));
        Viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        Render();
    }

    /// <summary>The Markdown source.</summary>
    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Shown (in italics) when <see cref="Markdown"/> is blank, e.g. "No description provided.".</summary>
    public string? EmptyText
    {
        get => (string?)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>True: the viewer scrolls by itself (fills its slot). False (default): it grows with its content.</summary>
    public bool IsScrollable
    {
        get => (bool)GetValue(IsScrollableProperty);
        set => SetValue(IsScrollableProperty, value);
    }

    /// <summary>Runs with the absolute URL of a clicked link (typically the view model's OpenLinkCommand).</summary>
    public ICommand? LinkCommand
    {
        get => (ICommand?)GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    /// <summary>Base for relative links ("docs/setup.md"), e.g. the repository URL.</summary>
    public string? BaseUrl
    {
        get => (string?)GetValue(BaseUrlProperty);
        set => SetValue(BaseUrlProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MarkdownViewer)d).Render();

    private static void OnScrollableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (MarkdownViewer)d;
        viewer.Viewer.VerticalScrollBarVisibility = (bool)e.NewValue ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private void Render()
    {
        Viewer.Document = MarkdownRenderer.Render(Markdown, EmptyText);
        var text = Markdown ?? EmptyText ?? string.Empty;
        AutomationProperties.SetHelpText(this, text.Length > 200 ? text[..200] : text);
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        var url = Resolve(e.Uri);
        if (url is null)
        {
            return;
        }

        if (LinkCommand is { } command)
        {
            if (command.CanExecute(url))
            {
                command.Execute(url);
            }

            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"Could not open {url}: {ex.Message}");
        }
    }

    /// <summary>The absolute http(s) or mailto URL of a link, or null when it can't be opened safely.</summary>
    private string? Resolve(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        if (!uri.IsAbsoluteUri)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl) || !Uri.TryCreate(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            {
                return null;
            }

            uri = new Uri(baseUri, uri);
        }

        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto ? uri.AbsoluteUri : null;
    }

    /// <summary>A viewer that doesn't scroll must not swallow the wheel: hand it to the scrolling pane around it.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsScrollable || e.Handled)
        {
            return;
        }

        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this,
        };
        (VisualTreeHelper.GetParent(this) as UIElement)?.RaiseEvent(forwarded);
    }
}
