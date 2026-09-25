using System.Windows;
using System.Windows.Controls;
using ForgeDesk.App.Controls.Diff;

namespace ForgeDesk.App.Controls;

/// <summary>Picks the hunk header or the code line template for a <see cref="DiffRow"/>.</summary>
public sealed class DiffRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HunkHeaderTemplate { get; set; }

    public DataTemplate? LineTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is DiffRow { IsHunkHeader: true } ? HunkHeaderTemplate : LineTemplate;
}
