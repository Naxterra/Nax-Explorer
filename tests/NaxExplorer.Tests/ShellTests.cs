using System.Reflection;
using System.Runtime.InteropServices;
using NaxExplorer.Core;
using NaxExplorer.Shell;

namespace NaxExplorer.Tests;

internal static class ShellTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("native_rename_preserves_payload_and_unrelated_siblings", "Renaming a generated file and folder through the Windows shell changes their names while sibling contents and modification times remain intact.", () =>
        {
            string root = Fixtures.TempFolder("native-rename");
            string source = Fixtures.Write(root, "before.txt", "rename payload");
            string sibling = Fixtures.Write(root, "sibling.txt", "unrelated payload");
            DateTime stamp = File.GetLastWriteTimeUtc(sibling);
            ShellActions.RenameAsync(source, "after.txt", IntPtr.Zero).GetAwaiter().GetResult();
            Check.That(!File.Exists(source), "old file name remained");
            Check.Equal("rename payload", File.ReadAllText(Path.Combine(root, "after.txt")));
            Fixtures.Write(root, @"old-folder\nested.txt", "nested payload");
            ShellActions.RenameAsync(Path.Combine(root, "old-folder"), "new-folder", IntPtr.Zero).GetAwaiter().GetResult();
            Check.Equal("nested payload", File.ReadAllText(Path.Combine(root, @"new-folder\nested.txt")));
            Check.Equal("unrelated payload", File.ReadAllText(sibling));
            Check.Equal(stamp, File.GetLastWriteTimeUtc(sibling));
        });

        yield return ("virtual_navigation_revokes_the_previous_folder_registration", "After a registered folder window navigates to This PC, Show in folder cannot find it under its old filesystem path.", () => OnSta(() =>
        {
            IntPtr window = Native.CreateWindowEx(0, "STATIC", "Folder registration test", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Check.That(window != IntPtr.Zero, "hidden test window could not be created");
            try
            {
                using var registration = new ShellWindowRegistration(window, (_, _) => { });
                registration.Navigated(Fixtures.TempFolder("registered-folder"));
                object entry = typeof(ShellWindowRegistration).GetField("_fromPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registration)!;
                int Cookie() => (int)entry.GetType().GetProperty("Cookie")!.GetValue(entry)!;
                Check.That(Cookie() != 0, "filesystem registration failed");
                registration.Navigated(PathTools.ThisPc);
                Check.Equal(0, Cookie(), "old filesystem registration remained active");
            }
            finally { Native.DestroyWindow(window); }
        }));

        yield return ("native_copy_move_and_cancel_report_progress", "The actual Windows copy engine copies and moves temporary files with progress callbacks; cancellation retains the source.", () =>
        {
            string root = Fixtures.TempFolder("native-transfer");
            string source = Fixtures.Write(root, @"from\note.txt", "sample content");
            string copied = Path.Combine(root, "copied"), moved = Path.Combine(root, "moved"), cancelled = Path.Combine(root, "cancelled");
            Directory.CreateDirectory(copied);
            Directory.CreateDirectory(moved);
            Directory.CreateDirectory(cancelled);
            var progress = new OperationProgress();
            int callbacks = 0;
            progress.Changed += (_, _) => Interlocked.Increment(ref callbacks);
            ShellActions.TransferAsync(TransferPlan.Plan([source], copied, false, null), false, IntPtr.Zero, progress).GetAwaiter().GetResult();
            Check.Equal("sample content", File.ReadAllText(Path.Combine(copied, "note.txt")));
            Check.That(callbacks > 0 && progress.Failures.Count == 0 && !progress.IsCancelled, "native copy did not report success through its sink");
            ShellActions.TransferAsync(TransferPlan.Plan([source], moved, true, null), true, IntPtr.Zero, new OperationProgress()).GetAwaiter().GetResult();
            Check.That(!File.Exists(source) && File.Exists(Path.Combine(moved, "note.txt")), "native move failed");
            var cancel = new OperationProgress();
            cancel.Changed += (_, _) => cancel.Cancel();
            string cancelSource = Path.Combine(moved, "note.txt");
            ShellActions.TransferAsync(TransferPlan.Plan([cancelSource], cancelled, true, null), true, IntPtr.Zero, cancel).GetAwaiter().GetResult();
            Check.That(cancel.IsCancelled && File.Exists(cancelSource) && !File.Exists(Path.Combine(cancelled, "note.txt")), "cancelled move did not retain its source");
        });

        yield return ("native_transfer_reports_an_unresolvable_source", "A requested source that vanishes before the shell parses it is reported as an error instead of silently dropping it from the batch.", () =>
        {
            string root = Fixtures.TempFolder("missing-source");
            string source = Fixtures.Write(root, @"from\exists.txt", "keep me");
            string missing = Path.Combine(root, @"from\missing.txt");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            bool failed = false;
            try
            {
                ShellActions.TransferAsync(TransferPlan.Plan([source, missing], target, true, null), true, IntPtr.Zero, new OperationProgress()).GetAwaiter().GetResult();
            }
            catch (COMException) { failed = true; }
            Check.That(failed, "missing source was silently omitted");
            Check.That(File.Exists(source), "a batch with an unresolved source partly moved files without reporting it first");
        });

        yield return ("native_copy_reports_a_locked_source_without_replacing_the_target", "A source held exclusively by another program fails visibly through the operation result or progress sink and leaves the destination empty.", () =>
        {
            string root = Fixtures.TempFolder("locked-source");
            string source = Fixtures.Write(root, "locked.txt", "locked content");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            var progress = new OperationProgress();
            bool failed = false;
            using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { ShellActions.TransferAsync(TransferPlan.Plan([source], target, false, null), false, IntPtr.Zero, progress).GetAwaiter().GetResult(); }
                catch (COMException) { failed = true; }
            }
            Check.That(failed || progress.Failures.Count > 0, "locked source was reported as successful");
            Check.That(File.Exists(source) && Directory.GetFileSystemEntries(target).Length == 0, "failed copy changed a source or left a partial destination");
        });
    }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check.That(thread.Join(TimeSpan.FromSeconds(15)), "shell test did not finish");
        if (error is not null) throw error;
    }
}
