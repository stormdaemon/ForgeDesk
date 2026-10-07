using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ForgeDesk.App.Theming;
using ForgeDesk.Presentation.Terminal;

namespace ForgeDesk.App.Features.Terminal;

/// <summary>
/// The Terminal tab. View-only plumbing: places each session's live terminal control (from
/// <see cref="TerminalHostRegistry"/>, never recreated) in the terminal area — stealing it from a
/// previous view after a project switch — shows only the selected one, keeps the card behind it the
/// same color as the terminal, focuses the terminal when the tab or session is shown, and maps
/// Ctrl+Shift+C / Ctrl+Shift+V to copy and paste.
/// </summary>
public partial class TerminalSectionView : UserControl
{
    private TerminalSectionViewModel? _viewModel;

    public TerminalSectionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded)
            {
                Attach(e.NewValue as TerminalSectionViewModel);
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeProbe.Changed += OnThemeChanged;
        UpdateCardBackground();
        Attach(DataContext as TerminalSectionViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeProbe.Changed -= OnThemeChanged;
        Detach();
    }

    private void Attach(TerminalSectionViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _viewModel))
        {
            SyncHosts();
            return;
        }

        Detach();
        if (viewModel is null)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.Sessions.CollectionChanged += OnSessionsChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncHosts();
        FocusSelected();
    }

    private void Detach()
    {
        if (_viewModel is { } viewModel)
        {
            viewModel.Sessions.CollectionChanged -= OnSessionsChanged;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncHosts();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TerminalSectionViewModel.SelectedSession):
                SyncHosts();
                FocusSelected();
                break;
            case nameof(TerminalSectionViewModel.FontSize):
                SyncHosts();
                break;
        }
    }

    /// <summary>Makes the terminal area hold exactly the sessions' controls, only the selected one visible.</summary>
    private void SyncHosts()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var wanted = new HashSet<UIElement>();
        foreach (var session in viewModel.Sessions)
        {
            if (TerminalHostRegistry.GetOrCreate(session) is not { } host)
            {
                continue;
            }

            host.SetFontSize(viewModel.FontSize);
            var control = host.Control;
            wanted.Add(control);
            if (!ReferenceEquals(control.Parent, TerminalStack))
            {
                host.DetachFromParent();
                TerminalStack.Children.Add(control);
            }

            control.Visibility = ReferenceEquals(session, viewModel.SelectedSession) ? Visibility.Visible : Visibility.Collapsed;
        }

        for (var i = TerminalStack.Children.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(TerminalStack.Children[i]))
            {
                TerminalStack.Children.RemoveAt(i);
            }
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            FocusSelected();
        }
    }

    private void FocusSelected()
    {
        if (IsVisible && TerminalHostRegistry.Find(_viewModel?.SelectedSession) is { } host)
        {
            host.Focus();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateCardBackground();

    private void UpdateCardBackground()
    {
        var brush = new SolidColorBrush(TerminalThemes.Background());
        brush.Freeze();
        TerminalCard.Background = brush;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)
            || TerminalHostRegistry.Find(_viewModel?.SelectedSession) is not { } host)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.C:
                host.CopySelection();
                e.Handled = true;
                break;
            case Key.V:
                host.Paste();
                e.Handled = true;
                break;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (TerminalHostRegistry.Find(_viewModel?.SelectedSession) is { } host)
        {
            host.CopySelection();
            host.Focus();
        }
    }

    private void OnPasteClick(object sender, RoutedEventArgs e)
    {
        if (TerminalHostRegistry.Find(_viewModel?.SelectedSession) is { } host)
        {
            host.Paste();
            host.Focus();
        }
    }

    private void OnShellMenuClick(object sender, RoutedEventArgs e) => OpenMenu("ShellMenu", ShellMenuButton);

    private void OnReplaceShellClick(object sender, RoutedEventArgs e) => OpenMenu("ReplaceShellMenu", ReplaceShellButton);

    private void OpenMenu(string key, FrameworkElement target)
    {
        if (Resources[key] is ContextMenu menu)
        {
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
