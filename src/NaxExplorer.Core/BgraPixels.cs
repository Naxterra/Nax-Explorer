namespace NaxExplorer.Core;

/// <summary>
/// Normalizes raw 32-bit BGRA bitmap buffers into premultiplied BGRA for WinUI WriteableBitmap.
/// </summary>
public static class BgraPixels
{
    /// <summary>
    /// Converts raw 32-bit BGRA pixels to premultiplied BGRA in place.
    /// Opaque bitmaps with zeroed alpha bytes become opaque, straight-alpha shell bitmaps (such as folder thumbnails
    /// whose transparent background carries white RGB with zero alpha) are premultiplied, and already-premultiplied
    /// PARGB32 bitmaps are preserved untouched.
    /// </summary>
    public static void Normalize(byte[] pixels)
    {
        bool anyAlpha = false;
        bool hasUnpremultiplied = false;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            if (a != 0) anyAlpha = true;
            if (b > a || g > a || r > a) hasUnpremultiplied = true;
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return;
        }
        if (!hasUnpremultiplied) return;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
            else if (a < 255)
            {
                pixels[i] = (byte)((pixels[i] * a + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * a + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * a + 127) / 255);
            }
        }
    }
}
