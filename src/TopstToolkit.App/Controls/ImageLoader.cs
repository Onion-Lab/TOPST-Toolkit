using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TopstToolkit.App.Controls;

/// <summary>
/// Loads pictures without keeping the files open (so the project can replace or delete them) and frozen
/// (so they can be made on a background thread and shown on the UI thread).
/// </summary>
public static class ImageLoader
{
    /// <summary>The picture decoded at a small size; null when the file is missing or not a picture.</summary>
    public static ImageSource? Thumbnail(string path, int decodeWidth = 160) => Load(path, decodeWidth);

    /// <summary>The picture at full size.</summary>
    public static BitmapSource? Full(string path) => Load(path, null);

    private static BitmapSource? Load(string path, int? decodeWidth)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth is { } w)
                image.DecodePixelWidth = w;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }
}
