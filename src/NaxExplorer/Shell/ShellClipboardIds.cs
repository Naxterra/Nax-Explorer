using System.Runtime.InteropServices;
using NaxExplorer.Core;

namespace NaxExplorer.Shell;

/// <summary>The standard Shell IDList Array clipboard format preserves virtual ZIP item identities without extracting.</summary>
internal static class ShellClipboardIds
{
    internal const string Format = "Shell IDList Array";

    internal static async Task<Windows.Storage.Streams.IRandomAccessStream> DragDataAsync(IReadOnlyList<string> paths)
    {
        byte[] data = Encode(paths);
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(data);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }

    internal static byte[] Encode(IReadOnlyList<string> paths)
    {
        using var items = ShellItems.Create(paths, requireAll: true);
        int[] sizes = items.Pidls.Select(p => checked((int)Native.ILGetSize(p))).ToArray();
        int header = checked(4 * (paths.Count + 2));
        byte[] data = new byte[checked(header + 2 + sizes.Sum())];
        BitConverter.TryWriteBytes(data.AsSpan(), paths.Count);
        BitConverter.TryWriteBytes(data.AsSpan(4), header);
        // An empty parent PIDL is the desktop, so each following PIDL is absolute.
        int offset = header + 2;
        for (int i = 0; i < paths.Count; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(8 + i * 4), offset);
            Marshal.Copy(items.Pidls[i], data, offset, sizes[i]);
            offset += sizes[i];
        }
        return data;
    }

    internal static List<string>? Decode(IntPtr data, int size)
    {
        if (size < 10) return null;
        int count = Marshal.ReadInt32(data);
        if (count <= 0 || count > (size - 8) / 4) return null;
        int header = (count + 2) * 4;
        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            int offset = Marshal.ReadInt32(data, 4 + i * 4);
            if (offset < header || offset > size - 2 || !ValidPidl(data, offset, size)) return null;
            offsets[i] = offset;
        }
        var paths = new List<string>(count);
        for (int i = 1; i <= count; i++)
        {
            IntPtr pidl = Native.ILCombine(data + offsets[0], data + offsets[i]);
            if (pidl == IntPtr.Zero) return null;
            try
            {
                if (Native.SHGetNameFromIDList(pidl, 0x80028000 /* DESKTOPABSOLUTEPARSING */, out IntPtr name) != 0 || name == IntPtr.Zero) return null;
                try
                {
                    string? path = Marshal.PtrToStringUni(name);
                    if (string.IsNullOrEmpty(path)) return null;
                    paths.Add(Path.IsPathFullyQualified(path) && Path.Exists(path) ? path : PathTools.ShellPlace(path));
                }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Native.ILFree(pidl); }
        }
        return paths;
    }

    private static bool ValidPidl(IntPtr data, int offset, int size)
    {
        while (offset <= size - 2)
        {
            int length = (ushort)Marshal.ReadInt16(data, offset);
            if (length == 0) return true;
            if (length < 2 || length > size - offset) return false;
            offset += length;
        }
        return false;
    }
}
