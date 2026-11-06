using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NaxExplorer.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NaxExplorer.Services;

/// <summary>
/// A decoded picture. <see cref="Width"/> and <see cref="Height"/> are the decoded pixels, which can be fewer than the
/// file holds; <see cref="OriginalWidth"/> and <see cref="OriginalHeight"/> are the picture's own size. Both are as shown,
/// after the camera's orientation tag and any turn in the viewer.
/// </summary>
internal sealed record LoadedImage(ImageSource Source, int Width, int Height, int OriginalWidth, int OriginalHeight, bool Animated);

/// <summary>
/// Decodes pictures with the Windows imaging codecs: JPEG, PNG, GIF, BMP, TIFF, ICO and WebP always, and HEIC, AVIF,
/// JPEG XL and camera RAW files when their free codecs from the Microsoft Store are installed. Photos are turned the
/// way the camera recorded, and colours are converted to sRGB. The file is opened for reading only.
/// </summary>
internal static class ImageLoader
{
    /// <summary>
    /// Loads a picture no larger than <paramref name="maxSide"/> pixels on its longer side (0 for full size, capped at
    /// 16384 pixels). GIF files keep their animation. Call on the UI thread.
    /// </summary>
    public static async Task<LoadedImage> LoadAsync(string path, int maxSide, CancellationToken token, int quarterTurns = 0)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        await using FileStream file = TextSniffer.OpenShared(path, 1 << 16);
        if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase) && maxSide == 0 && quarterTurns == 0)
        {
            // An animation reads its frames while it plays, so it gets a copy in memory that lives as long as the image.
            var memory = new InMemoryRandomAccessStream();
            await RandomAccessStream.CopyAsync(file.AsInputStream(), memory);
            memory.Seek(0);
            var gif = new BitmapImage();
            await gif.SetSourceAsync(memory);
            token.ThrowIfCancellationRequested();
            return new LoadedImage(gif, gif.PixelWidth, gif.PixelHeight, gif.PixelWidth, gif.PixelHeight, Animated: true);
        }
        using IRandomAccessStream stream = file.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        token.ThrowIfCancellationRequested();
        int width = (int)decoder.OrientedPixelWidth, height = (int)decoder.OrientedPixelHeight;
        (int originalWidth, int originalHeight) = quarterTurns % 2 == 1 ? (height, width) : (width, height);
        int limit = maxSide > 0 ? maxSide : 16384;
        // Turning in the viewer only changes how the picture is shown; the file is never changed.
        var transform = new BitmapTransform
        {
            InterpolationMode = BitmapInterpolationMode.Fant,
            Rotation = quarterTurns switch { 1 => BitmapRotation.Clockwise90Degrees, 2 => BitmapRotation.Clockwise180Degrees, 3 => BitmapRotation.Clockwise270Degrees, _ => BitmapRotation.None },
        };
        if (width > limit || height > limit)
        {
            double scale = (double)limit / Math.Max(width, height);
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        }
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        token.ThrowIfCancellationRequested();
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return new LoadedImage(source, bitmap.PixelWidth, bitmap.PixelHeight, originalWidth, originalHeight, Animated: false);
    }

    /// <summary>A message for a picture that could not be decoded, naming the Store codec when one is known to help.</summary>
    public static string ErrorText(string path, Exception error)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".heic" or ".heif" => Loc.T("CodecHeif"),
            ".avif" => Loc.T("CodecAv1"),
            ".jxl" => Loc.T("CodecJxl"),
            ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf" or ".srw" or ".pef" => Loc.T("CodecRaw"),
            _ => Loc.F("PictureFailed", error.Message),
        };
    }
}
