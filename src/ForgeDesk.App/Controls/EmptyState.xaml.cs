using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ForgeDesk.App.Controls;

/// <summary>
/// The "nothing here yet" state of a view: a Fluent icon, a one-line title, one sentence that
/// explains why it is empty, and the primary action that fixes it ("Create your first task").
/// </summary>
public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(EmptyState), new PropertyMetadata(SymbolRegular.Info24));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionCommandParameterProperty = DependencyProperty.Register(
        nameof(ActionCommandParameter), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryActionTextProperty = DependencyProperty.Register(
        nameof(SecondaryActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryActionCommandProperty = DependencyProperty.Register(
        nameof(SecondaryActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryActionCommandParameterProperty = DependencyProperty.Register(
        nameof(SecondaryActionCommandParameter), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

    public EmptyState()
    {
        InitializeComponent();
    }

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? ActionText
    {
        get => (string?)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public object? ActionCommandParameter
    {
        get => GetValue(ActionCommandParameterProperty);
        set => SetValue(ActionCommandParameterProperty, value);
    }

    public string? SecondaryActionText
    {
        get => (string?)GetValue(SecondaryActionTextProperty);
        set => SetValue(SecondaryActionTextProperty, value);
    }

    public ICommand? SecondaryActionCommand
    {
        get => (ICommand?)GetValue(SecondaryActionCommandProperty);
        set => SetValue(SecondaryActionCommandProperty, value);
    }

    public object? SecondaryActionCommandParameter
    {
        get => GetValue(SecondaryActionCommandParameterProperty);
        set => SetValue(SecondaryActionCommandParameterProperty, value);
    }
}
