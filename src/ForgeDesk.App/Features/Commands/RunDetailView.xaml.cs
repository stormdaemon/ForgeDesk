using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ForgeDesk.Presentation.Commands;

namespace ForgeDesk.App.Features.Commands;

/// <summary>
/// The selected run. View-only behavior: auto-scroll of the output while following (paused when
/// the user scrolls up, resumed by "Jump to latest" or by scrolling back to the end), bringing
/// search results into view, Ctrl+F / Enter / Shift+Enter / F3 / Esc for the search, and Ctrl+C to
/// copy the selected lines.
/// </summary>
public partial class RunDetailView : UserControl
{
    private RunLogViewModel? _log;
    private ScrollViewer? _scrollViewer;
    private bool _scrollPending;

    public RunDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as RunDetailViewModel)?.Log);
        Loaded += (_, _) =>
        {
            Attach((DataContext as RunDetailViewModel)?.Log);
            HookScrollViewer();
            ScrollToEndSoon();
        };
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(RunLogViewModel? log)
    {
        if (ReferenceEquals(_log, log))
        {
            return;
        }

        if (_log is not null)
        {
            _log.LinesAppended -= OnLinesAppended;
            _log.ScrollToEndRequested -= OnScrollToEndRequested;
            _log.ScrollToLineRequested -= OnScrollToLineRequested;
        }

        _log = log;
        if (log is not null)
        {
            log.LinesAppended += OnLinesAppended;
            log.ScrollToEndRequested += OnScrollToEndRequested;
            log.ScrollToLineRequested += OnScrollToLineRequested;
        }
    }

    private void HookScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            return;
        }

        LogList.ApplyTemplate();
        _scrollViewer = FindDescendant<ScrollViewer>(LogList);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnLogScrollChanged;
        }
    }

    private void OnLinesAppended(object? sender, EventArgs e)
    {
        if (_log is { IsFollowing: true })
        {
            ScrollToEndSoon();
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => ScrollToEndSoon();

    private void OnScrollToLineRequested(object? sender, LogLineViewModel line) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => LogList.ScrollIntoView(line));

    /// <summary>One scroll per burst of appended lines, after layout.</summary>
    private void ScrollToEndSoon()
    {
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            HookScrollViewer();
            _scrollViewer?.ScrollToEnd();
        });
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_log is null || _scrollViewer is null)
        {
            return;
        }

        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The user scrolled: follow only while at the end.
            var atEnd = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 2;
            if (_log.IsFollowing != atEnd)
            {
                _log.IsFollowing = atEnd;
            }
        }
        else if (_log.IsFollowing)
        {
            _scrollViewer.ScrollToEnd();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LogSearchBox.Focus();
            LogSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && _log is not null)
        {
            var command = Keyboard.Modifiers == ModifierKeys.Shift ? _log.PreviousMatchCommand : _log.NextMatchCommand;
            command.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (_log is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                var command = Keyboard.Modifiers == ModifierKeys.Shift ? _log.PreviousMatchCommand : _log.NextMatchCommand;
                command.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when _log.HasSearch:
                _log.ClearSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                LogList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnLogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelectedLines();
            e.Handled = true;
        }
    }

    private void OnCopySelectedLines(object sender, RoutedEventArgs e) => CopySelectedLines();

    private void CopySelectedLines()
    {
        var lines = LogList.SelectedItems.OfType<LogLineViewModel>().OrderBy(l => l.Index).Select(l => l.Text).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch (COMException)
        {
            // The clipboard is held by another application; the user can retry.
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
