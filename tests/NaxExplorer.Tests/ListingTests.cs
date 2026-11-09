using System.Diagnostics;
using NaxExplorer.Core;

namespace NaxExplorer.Tests;

internal static class ListingTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("drives_keep_letter_order_after_any_folder_sort", "Labels, capacity and a previous descending sort cannot reorder This PC's drives.", () =>
        {
            FileEntry Drive(string letter, string label, long size) => new() { Name = letter + ":", FullPath = letter + @":\", DisplayName = label, Kind = EntryKind.Drive, Size = size };
            var drives = new[] { Drive("E", "Alpha", 1), Drive("C", "Zulu", 300), Drive("I", "Card reader", 0), Drive("D", "Beta", 200), Drive("H", "USB", 0), Drive("F", "Archive", 900) };
            foreach (var column in Enum.GetValues<SortColumn>())
            foreach (bool descending in new[] { false, true })
            {
                var sorted = drives.ToList();
                EntrySorter.Sort(sorted, column, descending);
                Check.Sequence(new[] { "C:", "D:", "E:", "F:", "H:", "I:" }, sorted.Select(e => e.Name));
            }
        });

        yield return ("folder_row_sizes_count_nested_hidden_files_without_opening_content", "A size includes nested/hidden files, can count an exclusively locked file, and leaves contents and write times unchanged.", () =>
        {
            string root = Fixtures.TempFolder("row-sizes");
            string first = Fixtures.Write(root, "first.bin", new string('a', 13));
            string nested = Fixtures.Write(root, @"nested\hidden.bin", new string('b', 29));
            File.SetAttributes(nested, FileAttributes.Hidden);
            DateTime firstTime = File.GetLastWriteTimeUtc(first), nestedTime = File.GetLastWriteTimeUtc(nested);
            using (var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var size = FolderSizeReader.Measure(root, default);
                Check.That(size is { Bytes: 42, IsPartial: false }, "nested byte total is wrong or a locked file was skipped");
            }
            Check.Equal(firstTime, File.GetLastWriteTimeUtc(first));
            Check.Equal(nestedTime, File.GetLastWriteTimeUtc(nested));
            Check.Equal(new string('a', 13), File.ReadAllText(first));
            Check.Equal(new string('b', 29), File.ReadAllText(nested));
        });

        yield return ("folder_row_sizes_distinguish_empty_unavailable_and_cancelled", "Empty means zero, a missing folder or file root is unavailable, shares are not contacted, and cancelled work stops before counting.", () =>
        {
            string root = Fixtures.TempFolder("row-size-states");
            Check.That(FolderSizeReader.Measure(root, default) is { Bytes: 0, IsPartial: false }, "empty folder was not zero");
            string file = Fixtures.Write(root, "file.txt", "content");
            Check.That(FolderSizeReader.Measure(file, default) is null, "file root counted as an empty folder");
            Check.That(FolderSizeReader.Measure(Path.Combine(root, "missing"), default) is null, "missing folder counted as zero");
            Check.That(FolderSizeReader.Measure(@"\\nonexistent-test-host\share", default) is null, "share should require an explicit count");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool stopped = false;
            try { FolderSizeReader.Measure(root, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check.That(stopped, "cancelled count still ran");
        });

        yield return ("folder_row_sizes_skip_junction_roots_and_mark_nested_links_partial", "A junction to another folder is not scanned, including when it is the requested root; a skipped subtree is visibly partial.", () =>
        {
            string root = Fixtures.TempFolder("row-size-links");
            string outside = Fixtures.TempFolder("row-size-link-target");
            Fixtures.Write(root, "local.bin", "12345");
            Fixtures.Write(outside, "outside.bin", new string('x', 100));
            string link = Path.Combine(root, "junction");
            using var create = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            create.WaitForExit();
            if (create.ExitCode != 0 || !Directory.Exists(link)) throw new SkipException("Junction creation unavailable");
            try
            {
                Check.That(FolderSizeReader.Measure(root, default) is { Bytes: 5, IsPartial: true }, "junction target was counted or partial total was hidden");
                Check.That(FolderSizeReader.Measure(link, default) is null, "junction root was followed");
            }
            finally { Directory.Delete(link); }
        });
    }
}
