using System.Reflection;
using System.Runtime.InteropServices;
using NaxExplorer.Core;
using NaxExplorer.Shell;

namespace NaxExplorer.Tests;

internal static class ShellTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("file_drag_prepares_mixed_storage_items_without_changing_payload", "Files and folders, including Unicode names, become a complete ordered native drag payload while their contents and timestamps stay unchanged.", () =>
        {
            string root = Fixtures.TempFolder("drag-payload");
            string file = Fixtures.Write(root, "Ünicode note.txt", "drag payload");
            string child = Fixtures.Write(root, @"folder\child.txt", "nested payload");
            string folder = Path.GetDirectoryName(child)!;
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            string[] paths = [file, folder, child];
            var items = FileDragPayload.ReadAsync(paths).GetAwaiter().GetResult();
            Check.Sequence(paths, items.Select(item => item.Path));
            Check.That(items[0] is Windows.Storage.StorageFile && items[1] is Windows.Storage.StorageFolder, "native drag types were lost");
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            package.SetStorageItems(items);
            var view = package.GetView();
            Check.That(view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems), "storage format was not ready when the drag began");
            Check.Sequence(paths, view.GetStorageItemsAsync().AsTask().GetAwaiter().GetResult().Select(item => item.Path));
            Check.Equal("drag payload", File.ReadAllText(file));
            Check.Equal("nested payload", File.ReadAllText(child));
            Check.Equal(stamp, File.GetLastWriteTimeUtc(file));
        });
        yield return ("file_drag_rejects_incomplete_or_cancelled_payloads", "A missing selected file cannot silently become a partial drag, and cancellation prevents preparation from proceeding.", () =>
        {
            string root = Fixtures.TempFolder("drag-missing");
            string file = Fixtures.Write(root, "present.txt", "keep me");
            bool rejected = false;
            try { FileDragPayload.ReadAsync([file, Path.Combine(root, "missing.txt")]).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is FileNotFoundException or System.Runtime.InteropServices.COMException) { rejected = true; }
            Check.That(rejected, "missing file silently produced a partial payload");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool stopped = false;
            try { FileDragPayload.ReadAsync([file], cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            Check.That(stopped, "cancelled drag preparation continued");
            Check.Equal("keep me", File.ReadAllText(file));
        });
        yield return ("file_drag_items_that_take_too_long_are_left_for_later_but_missing_ones_still_fail", "Storage items resolved within the time limit come back complete and in order; when the limit runs out the answer is 'later' instead of an error, and a missing file still fails instead of being left out.", () =>
        {
            string root = Fixtures.TempFolder("drag-budget");
            string first = Fixtures.Write(root, "first.txt", "one");
            string second = Fixtures.Write(root, "second.txt", "two");
            var items = FileDragPayload.ReadWithinAsync([first, second], TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Check.Sequence(new[] { first, second }, items!.Select(item => item.Path));
            Check.That(FileDragPayload.ReadWithinAsync([first, second], TimeSpan.Zero).GetAwaiter().GetResult() is null, "an exhausted time limit was not reported as 'later'");
            bool rejected = false;
            try { FileDragPayload.ReadWithinAsync([first, Path.Combine(root, "missing.txt")], TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is FileNotFoundException or System.Runtime.InteropServices.COMException) { rejected = true; }
            Check.That(rejected, "a missing file was silently left out");
            Check.Equal("one", File.ReadAllText(first));
        });
        yield return ("file_drag_time_limit_holds_even_when_a_check_blocks_and_ignores_cancellation", "Work stuck in a call that ignores cancellation, as on a stalled network share, still gets the answer 'later' at the end of the time limit instead of holding the drag until the call returns.", () =>
        {
            using var release = new ManualResetEventSlim();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string? result = FileDragPayload.WithinAsync<string>(_ =>
            {
                release.Wait(TimeSpan.FromSeconds(10)); // blocks without looking at the token
                return Task.FromResult("too late");
            }, TimeSpan.FromMilliseconds(200)).GetAwaiter().GetResult();
            clock.Stop();
            release.Set();
            Check.That(result is null, "the stuck work was waited for");
            Check.That(clock.Elapsed < TimeSpan.FromSeconds(3), $"the answer took {clock.Elapsed.TotalSeconds:0.0} s instead of about 0.2 s");
            Check.Equal("in time", FileDragPayload.WithinAsync<string>(_ => Task.FromResult("in time"), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        });
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
