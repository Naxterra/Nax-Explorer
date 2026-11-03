using System.Globalization;
using System.Text;
using NaxExplorer.Core;

namespace NaxExplorer.Tests;

internal static class CoreTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("natural_sort_puts_file2_before_file10", "Names with numbers sort by the value of the numbers, ignoring case.", () =>
        {
            var names = new List<string> { "file10.txt", "File2.txt", "file1.txt", "file02b.txt", "alpha" };
            names.Sort(NaturalComparer.Instance);
            Check.Sequence(["alpha", "file1.txt", "File2.txt", "file02b.txt", "file10.txt"], names);
        });

        yield return ("folders_stay_before_files_in_both_sort_directions", "Folders come first whether the list is sorted up or down.", () =>
        {
            var entries = new List<FileEntry>
            {
                Entry("b.txt", EntryKind.File, 10), Entry("zeta", EntryKind.Folder, 0), Entry("a.txt", EntryKind.File, 99), Entry("alpha", EntryKind.Folder, 0),
            };
            EntrySorter.Sort(entries, SortColumn.Name, descending: false);
            Check.Sequence(["alpha", "zeta", "a.txt", "b.txt"], entries.Select(e => e.Name));
            EntrySorter.Sort(entries, SortColumn.Size, descending: true);
            Check.Sequence(["zeta", "alpha", "a.txt", "b.txt"], entries.Select(e => e.Name));
        });

        yield return ("history_back_and_forward_return_to_earlier_places", "Back and forward walk the visited folders in order.", () =>
        {
            var history = new NavigationHistory();
            history.Visit(@"C:\a");
            history.Visit(@"C:\b");
            history.Visit(@"C:\c");
            Check.Equal(@"C:\b", history.GoBack());
            Check.Equal(@"C:\a", history.GoBack());
            Check.Equal(@"C:\b", history.GoForward());
            Check.That(history.CanGoForward, "forward should still be possible");
        });

        yield return ("history_new_visit_after_back_clears_forward", "Visiting a new folder after going back drops the forward list, and revisiting the current folder changes nothing.", () =>
        {
            var history = new NavigationHistory();
            history.Visit(@"C:\a");
            history.Visit(@"C:\b");
            history.GoBack();
            history.Visit(@"C:\x");
            Check.That(!history.CanGoForward, "forward list should be empty");
            history.Visit(@"c:\X\");
            Check.Equal(1, history.BackEntries.Count, "back entries");
        });

        yield return ("address_bar_input_with_quotes_and_variables_resolves", "Typed paths lose their quotes, expand variables and resolve relative to the current folder.", () =>
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Check.Equal(profile, PathTools.Normalize("\"%USERPROFILE%\"", null));
            Check.Equal(@"C:\Windows\System32", PathTools.Normalize("System32", @"C:\Windows"));
            Check.Equal(@"C:\", PathTools.Normalize("..", @"C:\Windows"));
            Check.Equal(null, PathTools.Normalize("relative", PathTools.ThisPc));
        });

        yield return ("address_bar_bare_drive_letter_opens_drive_root", "Typing \"d:\" opens the root of drive D.", () =>
        {
            Check.Equal(@"D:\", PathTools.Normalize("d:", @"C:\Windows"));
        });

        yield return ("breadcrumb_lists_this_pc_drive_and_each_folder", "The address bar shows This PC, the drive and every folder of a path, and a network share as one root.", () =>
        {
            var segments = PathTools.Segments(@"C:\Users\Nax\Docs", "This PC");
            Check.Sequence(["This PC", "C:", "Users", "Nax", "Docs"], segments.Select(s => s.Label));
            Check.Equal(@"C:\Users\Nax", segments[3].Location);
            var share = PathTools.Segments(@"\\server\share\folder", "This PC");
            Check.Sequence(["This PC", @"\\server\share", "folder"], share.Select(s => s.Label));
        });

        yield return ("going_up_from_a_drive_root_shows_this_pc", "The parent of C:\\ is This PC, and This PC has no parent.", () =>
        {
            Check.Equal(PathTools.ThisPc, PathTools.Parent(@"C:\"));
            Check.Equal(@"C:\Users", PathTools.Parent(@"C:\Users\Nax"));
            Check.Equal(null, PathTools.Parent(PathTools.ThisPc));
        });

        yield return ("names_windows_forbids_are_rejected", "Empty names, forbidden characters, reserved device names and trailing dots are refused.", () =>
        {
            Check.Equal(NameProblem.None, NameRules.Check("Report 2026.docx"));
            Check.Equal(NameProblem.Empty, NameRules.Check("  "));
            Check.Equal(NameProblem.InvalidCharacters, NameRules.Check("a:b"));
            Check.Equal(NameProblem.ReservedName, NameRules.Check("con.txt"));
            Check.Equal(NameProblem.ReservedName, NameRules.Check("LPT1"));
            Check.Equal(NameProblem.EndsWithDotOrSpace, NameRules.Check("name."));
        });

        yield return ("new_item_name_gets_a_number_before_the_extension", "A taken name becomes \"name (2).ext\", and folders keep dots in their names.", () =>
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "New Text.txt", "New Text (2).txt", "v1.0" };
            Check.Equal("New Text (3).txt", NameRules.Unused("New Text.txt", isFolder: false, taken.Contains));
            Check.Equal("v1.0 (2)", NameRules.Unused("v1.0", isFolder: true, taken.Contains));
            Check.Equal("free.txt", NameRules.Unused("free.txt", isFolder: false, taken.Contains));
        });

        yield return ("sizes_are_shown_in_binary_units", "Byte counts read as B, KB, MB and GB with at most one decimal.", () =>
        {
            var culture = CultureInfo.InvariantCulture;
            Check.Equal("512 B", SizeFormat.Format(512, culture));
            Check.Equal("1.5 KB", SizeFormat.Format(1536, culture));
            Check.Equal("120 MB", SizeFormat.Format(120L * 1024 * 1024, culture));
            Check.Equal("2 GB", SizeFormat.Format(2L * 1024 * 1024 * 1024, culture));
        });

        yield return ("text_encodings_are_recognized_from_the_first_bytes", "UTF-16 without a byte order mark, UTF-8 and Windows-1252 text are told apart, and binary data is refused.", () =>
        {
            Check.Equal(Encoding.Unicode.WebName, TextSniffer.Detect(Encoding.Unicode.GetBytes("Hello world, plain text"))?.WebName);
            Check.Equal(Encoding.UTF8.WebName, TextSniffer.Detect(Encoding.UTF8.GetBytes("Grüße aus Köln"))?.WebName);
            Check.Equal(TextSniffer.Legacy.WebName, TextSniffer.Detect(TextSniffer.Legacy.GetBytes("Grüße aus Köln"))?.WebName);
            Check.Equal(null, TextSniffer.Detect([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00]));
        });

        yield return ("listing_hides_hidden_and_protected_files_unless_asked", "Hidden files appear only when asked, and hidden system files only when protected files are shown too.", () =>
        {
            string folder = Fixtures.TempFolder("listing");
            Fixtures.Write(folder, "visible.txt", "a");
            string hidden = Fixtures.Write(folder, "hidden.txt", "b");
            string system = Fixtures.Write(folder, "desktop.ini", "c");
            File.SetAttributes(hidden, FileAttributes.Hidden);
            File.SetAttributes(system, FileAttributes.Hidden | FileAttributes.System);
            Check.Sequence(["visible.txt"], DirectoryReader.Read(folder, new ListingOptions(false)).Select(e => e.Name));
            Check.Sequence(["hidden.txt", "visible.txt"], DirectoryReader.Read(folder, new ListingOptions(true)).Select(e => e.Name).Order());
            Check.Equal(3, DirectoryReader.Read(folder, new ListingOptions(true, true)).Count, "with protected files");
        });

        yield return ("folder_changes_report_added_removed_and_changed_items", "Comparing two listings names exactly the items that appeared, vanished or changed.", () =>
        {
            var before = new List<FileEntry> { Entry("a", EntryKind.File, 1), Entry("b", EntryKind.File, 2), Entry("c", EntryKind.File, 3) };
            var after = new List<FileEntry> { Entry("a", EntryKind.File, 1), Entry("b", EntryKind.File, 20), Entry("d", EntryKind.File, 4) };
            FolderChanges changes = FolderChanges.Compute(before, after);
            Check.Sequence(["c"], changes.Removed.Select(e => e.Name));
            Check.Sequence(["d"], changes.Added.Select(e => e.Name));
            Check.Sequence(["b"], changes.Changed.Select(e => e.Name));
        });

        yield return ("moving_a_folder_into_its_own_subfolder_is_refused", "A copy or move into the folder itself or one of its subfolders never starts.", () =>
        {
            string folder = Fixtures.TempFolder("transfer");
            string inner = Path.Combine(folder, "inner");
            Directory.CreateDirectory(inner);
            Check.Equal(TransferProblem.IntoItself, TransferCheck.Check([folder], inner, move: true));
            Check.Equal(TransferProblem.IntoItself, TransferCheck.Check([folder], folder, move: false));
        });

        yield return ("moving_items_to_their_own_folder_is_nothing_to_do", "Moving files to the folder they are already in does nothing, while copying there is allowed.", () =>
        {
            string folder = Fixtures.TempFolder("same");
            string file = Fixtures.Write(folder, "a.txt", "x");
            Check.Equal(TransferProblem.AlreadyThere, TransferCheck.Check([file], folder, move: true));
            Check.Equal(TransferProblem.None, TransferCheck.Check([file], folder, move: false));
        });

        yield return ("permanent_delete_never_allows_undo_so_windows_asks_first", "Shift+Delete hands Windows a delete without undo and without suppressed confirmation, so Windows deletes for good only after asking.", () =>
        {
            uint flags = OperationFlags.ForDelete(permanently: true);
            Check.That((flags & OperationFlags.AllowUndo) == 0, "permanent delete allows undo, so it would go to the Recycle Bin");
            Check.That((flags & OperationFlags.RecycleOnDelete) == 0, "permanent delete asks for the Recycle Bin");
            Check.That((flags & 0x10 /* FOF_NOCONFIRMATION */) == 0, "the confirmation is suppressed");
        });

        yield return ("normal_delete_goes_to_the_recycle_bin", "Delete without Shift always goes to the Recycle Bin and can be undone, and copies and renames can be undone too.", () =>
        {
            uint flags = OperationFlags.ForDelete(permanently: false);
            Check.That((flags & OperationFlags.AllowUndo) != 0 && (flags & OperationFlags.RecycleOnDelete) != 0, "delete does not use the Recycle Bin");
            Check.That((OperationFlags.ForTransfer(false) & OperationFlags.AllowUndo) != 0, "copy cannot be undone");
            Check.That((OperationFlags.ForTransfer(false) & OperationFlags.RenameOnCollision) == 0, "copies elsewhere rename silently instead of asking");
            Check.That((OperationFlags.ForTransfer(true) & OperationFlags.RenameOnCollision) != 0, "copies into the same folder ask instead of naming them - Copy");
            Check.That((OperationFlags.ForRename() & OperationFlags.AllowUndo) != 0, "rename cannot be undone");
        });

        yield return ("damaged_settings_file_gives_default_settings", "A settings file that is not valid JSON never stops the app; the defaults are used.", () =>
        {
            string folder = Fixtures.TempFolder("settings");
            string path = Fixtures.Write(folder, "settings.json", "{ this is not json");
            AppSettings settings = AppSettings.Load(path);
            Check.That(settings.DualPane && settings.ShowExtensions, "defaults expected");
        });

        yield return ("saved_settings_round_trip_and_leave_no_temporary_file", "Saving writes through a temporary file that is gone afterwards, and the values load back unchanged.", () =>
        {
            string folder = Fixtures.TempFolder("settings");
            string path = Path.Combine(folder, "settings.json");
            var settings = new AppSettings { DualPane = false, Pinned = [@"C:\Work"], IndexedFolders = [@"D:\Docs"] };
            settings.Left.Sort = SortColumn.Size;
            settings.Save(path);
            Check.That(!File.Exists(path + ".tmp"), "temporary file left behind");
            AppSettings loaded = AppSettings.Load(path);
            Check.Equal(false, loaded.DualPane, "dual pane");
            Check.Equal(@"C:\Work", loaded.Pinned.Single(), "pinned");
            Check.Equal(SortColumn.Size, loaded.Left.Sort, "sort");
        });

        yield return ("reading_a_file_never_blocks_other_programs", "While the app reads a file, other programs can still write to it and delete it.", () =>
        {
            string folder = Fixtures.TempFolder("share");
            string path = Fixtures.Write(folder, "busy.txt", "content");
            using (FileStream reading = TextSniffer.OpenShared(path))
            {
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) writer.WriteByte(65);
                File.Delete(path);
            }
            Check.That(!File.Exists(path), "the file should be deleted");
        });
    }

    private static FileEntry Entry(string name, EntryKind kind, long size) => new()
    {
        Name = name,
        FullPath = @"C:\test\" + name,
        Kind = kind,
        Size = size,
        ModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
