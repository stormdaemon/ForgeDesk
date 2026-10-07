using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using ForgeDesk.App.Controls;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.App.Features.Files;

/// <summary>
/// Loads an image file for the preview without locking it (the file can still be edited, renamed
/// or deleted) and without the WPF image cache (a changed file shows its new content). Returns null
/// when the file cannot be decoded; the view then shows a notice.
/// </summary>
public sealed class FileImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or InvalidOperationException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps a <see cref="StatusTone"/> to the <see cref="StatusKind"/> of Pill and StatusDot.</summary>
public sealed class ToneToStatusKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusTone.Success => StatusKind.Success,
        StatusTone.Warning => StatusKind.Warning,
        StatusTone.Danger => StatusKind.Danger,
        StatusTone.Info => StatusKind.Info,
        StatusTone.Running => StatusKind.Running,
        _ => StatusKind.Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Chevron of a tree row: visible for folders, hidden (keeping its slot so names align) for files,
/// collapsed while the folder is being listed (a spinner takes its place).
/// Values: HasChevron, IsLoading.
/// </summary>
public sealed class ChevronVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasChevron = values is { Length: > 0 } && values[0] is true;
        var isLoading = values is { Length: > 1 } && values[1] is true;
        return isLoading ? System.Windows.Visibility.Collapsed : hasChevron ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>"1920 × 1080 px" for a decoded image, or nothing.</summary>
public sealed class ImageDimensionsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is BitmapSource image ? $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px" : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
