using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace JarvisCode.App.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// File path → decoded thumbnail bitmap. Loaded with OnLoad caching so the file
/// is not locked, and decoded small so a 4K screenshot costs kilobytes, not MBs.
/// </summary>
public sealed class ImageThumbnailConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
        {
            return null;
        }

        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = parameter is string s && int.TryParse(s, out var height) ? height : 96;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException
                                       or UnauthorizedAccessException or UriFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Tool-result <see cref="JarvisCode.Core.Models.ImageBlock"/> → decoded bitmap,
/// capped small: transcript rows show screenshots as thumbnails, like the reference.
/// </summary>
public sealed class ImageBlockConverter : IValueConverter
{
    /// <summary>The reference caps a tool result's image at <c>max-h-[360px]</c>.</summary>
    public int DecodeHeight { get; set; } = 360;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not JarvisCode.Core.Models.ImageBlock image || image.Base64Data.Length == 0)
        {
            return null;
        }

        try
        {
            var bytes = System.Convert.FromBase64String(image.Base64Data);
            // DecodePixelHeight resamples rather than caps, so asking for it
            // unconditionally would blow a small screenshot up past its own size —
            // the reference shows an image at its natural size under a 360px
            // ceiling. Read the frame's height first and only ask when it is over.
            var natural = System.Windows.Media.Imaging.BitmapFrame
                .Create(
                    new System.IO.MemoryStream(bytes),
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                    System.Windows.Media.Imaging.BitmapCacheOption.None)
                .PixelHeight;

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new System.IO.MemoryStream(bytes);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            if (natural > DecodeHeight)
            {
                bitmap.DecodePixelHeight = DecodeHeight;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or System.IO.IOException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Picks the markdown renderer a surface uses. The reference ships two and the
/// app has the same two surfaces, so the Code transcript renders with
/// <see cref="Controls.MarkdownProfile.Code"/> and a chat with
/// <see cref="Controls.MarkdownProfile.Chat"/>.
/// </summary>
public sealed class SurfaceMarkdownProfileConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Controls.MarkdownProfile.Code : Controls.MarkdownProfile.Chat;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
