using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NaxExplorer.Core;

public static class NewItemCreator
{
    /// <summary>Reserves a fresh name, including when two creation requests race for the same folder.</summary>
    public static string Create(string parent, string wanted, bool folder)
    {
        if (NameRules.Check(wanted) != NameProblem.None) throw new ArgumentException("Invalid new item name.", nameof(wanted));
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string name = NameRules.Unused(wanted, folder, candidate => Path.Exists(Path.Combine(parent, candidate)));
            string path = Path.Combine(parent, name);
            if (folder)
            {
                string full = Path.GetFullPath(path);
                string native = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
                    : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
                if (CreateDirectory(native, IntPtr.Zero)) return path;
                int error = Marshal.GetLastWin32Error();
                if (error == 183) continue; // another request reserved that name; never reuse its folder
                throw new IOException(new Win32Exception(error).Message, new Win32Exception(error));
            }
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                return path;
            }
            catch (IOException) when (Path.Exists(path)) { }
        }
        throw new IOException("A new name could not be reserved. Please try again.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
