using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NaxExplorer.Shell;

/// <summary>Pixels of an icon or thumbnail in premultiplied BGRA order, ready for a WriteableBitmap.</summary>
internal sealed record ImagePixels(int Width, int Height, byte[] Bgra);

/// <summary>
/// Gets icons and thumbnails from Windows, the same ones Explorer shows, including thumbnails of videos, PDFs and
/// Office files. Requests run on two background STA threads, newest first, so the rows on screen are served before
/// rows that were scrolled past; a request whose row left the screen is skipped.
/// </summary>
internal static class ShellImages
{
    private sealed record Request(string Path, int Size, bool Thumbnail, Func<bool> StillWanted, Action<ImagePixels?> Done);

    private static readonly ConcurrentStack<Request> Pending = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static int _started;

    public static void Enqueue(string path, int size, bool thumbnail, Func<bool> stillWanted, Action<ImagePixels?> done)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            for (int i = 0; i < 2; i++)
            {
                var thread = new Thread(Work) { IsBackground = true, Name = "Shell images " + i, Priority = ThreadPriority.BelowNormal };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }
        Pending.Push(new Request(path, size, thumbnail, stillWanted, done));
        Signal.Release();
    }

    private static void Work()
    {
        while (true)
        {
            Signal.Wait();
            if (!Pending.TryPop(out Request? request)) continue;
            if (!request.StillWanted())
            {
                // The row left the screen; the caller asks again when it comes back.
                request.Done(null);
                continue;
            }
            ImagePixels? pixels = null;
            try
            {
                pixels = Get(request.Path, request.Size, request.Thumbnail);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or UnauthorizedAccessException)
            {
                // A broken thumbnail handler gives no picture, never a crash.
            }
            request.Done(pixels);
        }
    }

    /// <summary>Gets one image on the calling thread, which must be an STA thread.</summary>
    public static ImagePixels? Get(string path, int size, bool thumbnail)
    {
        Guid riid = Native.IID_IShellItem;
        if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref riid, out IShellItem item) != 0) return null;
        try
        {
            if (item is not IShellItemImageFactory factory) return null;
            uint flags = thumbnail ? Native.SIIGBF_RESIZETOFIT : Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK;
            if (factory.GetImage(new Native.SIZE { cx = size, cy = size }, flags, out IntPtr bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            try
            {
                return Read(bitmap);
            }
            finally
            {
                Native.DeleteObject(bitmap);
            }
        }
        finally
        {
            Native.Release(item);
        }
    }

    private static ImagePixels? Read(IntPtr bitmap)
    {
        if (Native.GetObject(bitmap, Marshal.SizeOf<Native.BITMAP>(), out Native.BITMAP info) == 0) return null;
        int width = info.bmWidth, height = Math.Abs(info.bmHeight);
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        byte[] pixels = new byte[width * height * 4];
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, dc);
        }
        // Images without an alpha channel, such as many photo thumbnails, come back fully transparent: make them opaque.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        return new ImagePixels(width, height, pixels);
    }

    /// <summary>The type name Windows shows for a file name, such as "Text Document", without touching the disk.</summary>
    public static string TypeName(string nameOrExtension, bool folder)
    {
        var info = new Native.SHFILEINFO();
        uint attributes = folder ? 0x10u /* FILE_ATTRIBUTE_DIRECTORY */ : 0x80u /* FILE_ATTRIBUTE_NORMAL */;
        Native.SHGetFileInfo(nameOrExtension, attributes, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
        return info.szTypeName ?? "";
    }
}
