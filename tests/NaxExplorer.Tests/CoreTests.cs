using System.Diagnostics;
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

        yield return ("customized_folder_shows_its_localized_name", "A folder whose desktop.ini names a localized name shows that name, as Explorer shows \"Bilder\" for Pictures.", () =>
        {
            string parent = Fixtures.TempFolder("localized");
            string folder = Path.Combine(parent, "Pictures-Test");
            Directory.CreateDirectory(folder);
            string ini = Fixtures.Write(folder, "desktop.ini", "[.ShellClassInfo]\r\nLocalizedResourceName=Meine Bilder\r\n", System.Text.Encoding.Unicode);
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(folder, FileAttributes.ReadOnly | FileAttributes.Directory);
            try
            {
                FileEntry entry = DirectoryReader.Read(parent, new ListingOptions(false)).Single();
                Check.Equal("Pictures-Test", entry.Name, "file system name");
                Check.Equal("Meine Bilder", entry.DisplayName, "shown name");
            }
            finally
            {
                File.SetAttributes(folder, FileAttributes.Directory);
                File.SetAttributes(ini, FileAttributes.Normal);
            }
        });

        yield return ("drives_show_windows_name_with_their_letter", "Drives carry the name Windows shows for them, which ends with their letter, such as \"Lokaler Datenträger (C:)\".", () =>
        {
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
            FileEntry system = DirectoryReader.ReadDrives().First(d => string.Equals(d.FullPath, systemRoot, StringComparison.OrdinalIgnoreCase));
            Check.That(system.DisplayName.Contains($"({system.Name})"), $"unexpected drive name: {system.DisplayName}");
        });

        yield return ("drive_name_follows_a_replaced_or_renamed_drive", "When a drive letter gets another medium or a new label, the drive shows its current name, not the one stored earlier.", () =>
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory)!;
            ShellNames.Store(root, "OLD STICK (X:)");
            string name = ShellNames.Of(root);
            Check.That(name != "OLD STICK (X:)" && name.Contains($"({root.TrimEnd('\\')})"), $"stale drive name: {name}");
            Check.Equal(name, ShellNames.Known(root), "stored name");
        });

        yield return ("address_bar_asks_the_system_drive_every_time_and_a_known_share_never_again", "The address bar gets the current name of the system drive even when an old one is stored, while a network share whose name is already known is not asked again, because it can take seconds to answer.", () =>
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory)!;
            Check.That(ShellNames.IsLocalDrive(root), $"{root} should count as a drive in this PC");
            Check.That(!ShellNames.IsLocalDrive(@"\\server\share\"), "a network share counted as a drive in this PC");
            ShellNames.Store(root, "OLD DISC (X:)");
            string name = ShellNames.OfDrive(root);
            Check.That(name != "OLD DISC (X:)" && name.Contains($"({root.TrimEnd('\\')})"), $"stale drive name: {name}");
            const string share = @"\\server\share\";
            ShellNames.Store(share, @"share (\\server)");
            Check.Equal(@"share (\\server)", ShellNames.OfDrive(share), "a known share name was asked again");
        });

        yield return ("failed_name_lookup_is_not_stored_so_it_is_tried_again", "When Windows gives no name, for example for a share that does not answer, the plain name is shown but not stored, so the next step asks again.", () =>
        {
            string missing = Path.Combine(Fixtures.TempFolder("names"), "Missing Folder");
            Check.Equal("Missing Folder", ShellNames.Of(missing), "plain name for a failed lookup");
            Check.That(!ShellNames.IsStored(missing), "a failed lookup was stored and would never be tried again");
        });

        yield return ("plain_folder_keeps_its_name_and_the_name_store_stays_bounded", "A folder without the read-only or system attribute keeps its own name, and the store of names never grows past its limit during big searches.", () =>
        {
            string folder = Directory.CreateDirectory(Path.Combine(Fixtures.TempFolder("names"), "Plain Folder")).FullName;
            Check.Equal("Plain Folder", ShellNames.OfFolder(folder), "plain folder");
            for (int i = 0; i <= ShellNames.MaxStored; i++) ShellNames.Store($@"X:\store-test\{i}", "name");
            Check.That(ShellNames.StoredCount <= ShellNames.MaxStored, $"the store grew to {ShellNames.StoredCount} names");
        });

        yield return ("title_path_uses_the_folder_names_windows_shows", "The path in the title reads C:\\Benutzer\\Anna for C:\\Users\\Anna, and a share keeps its server and share name.", () =>
        {
            var shown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Users"] = "Benutzer",
                [@"\\server\share\Users"] = "Benutzer",
            };
            string ShownName(string path) => shown.TryGetValue(path, out string? name) ? name : "";
            Check.Equal(@"C:\Benutzer\Anna", PathTools.DisplayPath(@"C:\Users\Anna\", ShownName), "local path");
            Check.Equal(@"C:\", PathTools.DisplayPath(@"C:\", ShownName), "drive root");
            Check.Equal(@"\\server\share\Benutzer\x", PathTools.DisplayPath(@"\\server\share\Users\x", ShownName), "network share");
            Check.Equal(PathTools.ThisPc, PathTools.DisplayPath(PathTools.ThisPc, ShownName), "This PC");
        });

        yield return ("search_results_keep_their_order_until_a_column_is_chosen", "Search results stay in the order found, which is the relevance, until a column is chosen; then they sort by name, date or size in either direction.", () =>
        {
            var found = new List<FileEntry>
            {
                new() { Name = "c.pdf", DisplayName = "c.pdf", FullPath = @"C:\docs\c.pdf", Kind = EntryKind.File, Size = 200, ModifiedUtc = new DateTime(2024, 1, 1) },
                new() { Name = "a.pdf", DisplayName = "a.pdf", FullPath = @"C:\docs\a.pdf", Kind = EntryKind.File, Size = 300, ModifiedUtc = new DateTime(2022, 1, 1) },
                new() { Name = "b.pdf", DisplayName = "b.pdf", FullPath = @"C:\docs\b.pdf", Kind = EntryKind.File, Size = 100, ModifiedUtc = new DateTime(2023, 1, 1) },
            };
            string Names(SortColumn? column, bool descending) => string.Join(",", EntrySorter.Order(found, e => e, column, descending).Select(e => e.Name));
            Check.Equal("c.pdf,a.pdf,b.pdf", Names(null, false), "order found");
            Check.Equal("a.pdf,b.pdf,c.pdf", Names(SortColumn.Name, false), "by name");
            Check.Equal("c.pdf,b.pdf,a.pdf", Names(SortColumn.Modified, true), "newest first");
            Check.Equal("a.pdf,c.pdf,b.pdf", Names(SortColumn.Size, true), "largest first");
        });

        yield return ("folder_totals_count_everything_below_and_never_follow_a_junction", "Counting a folder finds every subfolder and file below it, hidden ones too, adds up their sizes, and does not walk into a junction that points back to the folder.", () =>
        {
            string root = Fixtures.TempFolder("totals");
            Fixtures.Write(root, "a.txt", new string('a', 10));
            Fixtures.Write(root, @"sub\b.txt", new string('b', 20));
            string hidden = Fixtures.Write(root, @"sub\deeper\c.txt", new string('c', 5));
            File.SetAttributes(hidden, FileAttributes.Hidden);
            string link = Path.Combine(root, "loop");
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{root}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            try
            {
                bool junction = Directory.Exists(link);
                FolderTotals totals = FolderStats.Measure([root], CancellationToken.None);
                Check.Equal(junction ? 3L : 2L, totals.Folders, "folders (sub, deeper and the junction itself)");
                Check.Equal(3L, totals.Files, "files");
                Check.Equal(35L, totals.Bytes, "bytes");
                Check.That(totals.Complete, "the count did not finish");
                FolderTotals selection = FolderStats.Measure([Path.Combine(root, "a.txt"), Path.Combine(root, "sub")], CancellationToken.None);
                Check.Equal(1L, selection.Folders, "folders below the selection");
                Check.Equal(3L, selection.Files, "a.txt plus the two files below sub");
                Check.Equal(35L, selection.Bytes, "bytes of the selection");
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                File.SetAttributes(hidden, FileAttributes.Normal);
            }
        });

        yield return ("quick_access_can_be_added_to_reordered_trimmed_and_reset", "Quick access starts with Windows' folders and the folders pinned before; a folder is added once, entries move and go away, the order survives saving, and reset brings the list back as it came.", () =>
        {
            var settings = new AppSettings { Pinned = [@"C:\Work"] };
            Check.Sequence([.. QuickAccessList.Defaults, @"C:\Work"], QuickAccessList.Current(settings));
            QuickAccessList.Add(settings, @"c:\work\");
            QuickAccessList.Add(settings, @"D:\Projects");
            QuickAccessList.Remove(settings, QuickAccessList.Music);
            QuickAccessList.Move(settings, @"D:\Projects", -100);
            QuickAccessList.Move(settings, QuickAccessList.Home, 1);
            List<string> list = QuickAccessList.Current(settings);
            Check.Equal(@"D:\Projects", list[0], "moved to the top");
            Check.Equal(QuickAccessList.Desktop, list[1], "Desktop above Home after Home moved down");
            Check.Equal(QuickAccessList.Home, list[2], "Home one step down");
            Check.That(!list.Contains(QuickAccessList.Music), "Music was not removed");
            Check.Equal(1, list.Count(e => e.Equals(@"C:\Work", StringComparison.OrdinalIgnoreCase)), "the pinned folder appears once");
            Check.Equal(0, settings.Pinned.Count, "the old pinned list was not taken over");
            string path = Path.Combine(Fixtures.TempFolder("quick"), "settings.json");
            settings.Save(path);
            Check.Sequence(list, QuickAccessList.Current(AppSettings.Load(path)));
            QuickAccessList.Reset(settings);
            Check.Sequence(QuickAccessList.Defaults, QuickAccessList.Current(settings));
        });

        yield return ("explorer_replacement_takes_over_folders_and_win_e_and_gives_everything_back", "Turning folder opening over to the app points folders, drives and Win+E at it, and turning it off puts back a default verb and a Win+E command that were there before and leaves no key of its own behind.", () =>
        {
            const string scratch = @"Software\NaxExplorerTests";
            string name = Guid.NewGuid().ToString("N");
            using Microsoft.Win32.RegistryKey parent = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch, writable: true);
            try
            {
                using Microsoft.Win32.RegistryKey classes = parent.CreateSubKey(name, writable: true);
                // What another program set before: a default verb for drives and its own Win+E command.
                using (Microsoft.Win32.RegistryKey drive = classes.CreateSubKey(@"Drive\shell", writable: true)) drive.SetValue("", "otherverb");
                string winE = $@"CLSID\{ExplorerReplacement.WinEClsid}\shell\opennewwindow\command";
                using (Microsoft.Win32.RegistryKey command = classes.CreateSubKey(winE, writable: true)) command.SetValue("", "other.exe");
                var replacement = new ExplorerReplacement(classes);
                Check.Equal(null, replacement.RegisteredProgram(), "nothing registered yet");
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                Check.Equal(@"C:\Apps\Nax-Explorer.exe", replacement.RegisteredProgram(), "registered program");
                foreach (string type in new[] { "Directory", "Drive" })
                {
                    using Microsoft.Win32.RegistryKey verb = classes.OpenSubKey($@"{type}\shell\{ExplorerReplacement.Verb}\command")!;
                    Check.Equal("\"C:\\Apps\\Nax-Explorer.exe\" \"%1\"", verb.GetValue("") as string, type);
                }
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(winE)!)
                {
                    Check.Equal("\"C:\\Apps\\Nax-Explorer.exe\"", command.GetValue("") as string, "Win+E command");
                    Check.Equal("", command.GetValue("DelegateExecute") as string, "Win+E runs the command");
                }
                replacement.TurnOff();
                Check.Equal(null, replacement.RegisteredProgram(), "still registered after turning off");
                using (Microsoft.Win32.RegistryKey drive = classes.OpenSubKey(@"Drive\shell")!) Check.Equal("otherverb", drive.GetValue("") as string, "the drive verb set before");
                Check.That(classes.OpenSubKey(@"Directory") is null, "an empty Directory key was left behind");
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(winE)!)
                {
                    Check.Equal("other.exe", command.GetValue("") as string, "the Win+E command set before");
                    Check.Equal(null, command.GetValue("DelegateExecute"), "a DelegateExecute that was not there before");
                }
                using (Microsoft.Win32.RegistryKey open = classes.OpenSubKey($@"CLSID\{ExplorerReplacement.WinEClsid}\shell\opennewwindow")!)
                {
                    Check.Equal(0, open.ValueCount, "the app's notes on the Win+E key");
                }
                // Without anything set before, turning off removes every key the app made.
                using Microsoft.Win32.RegistryKey clean = parent.CreateSubKey(name + "-clean", writable: true);
                var fresh = new ExplorerReplacement(clean);
                fresh.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                fresh.TurnOff();
                Check.Equal(0, clean.SubKeyCount, "keys left behind");
            }
            finally
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                parent.DeleteSubKeyTree(name + "-clean", throwOnMissingSubKey: false);
                using Microsoft.Win32.RegistryKey software = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: true)!;
                if (parent.SubKeyCount == 0) software.DeleteSubKey("NaxExplorerTests", throwOnMissingSubKey: false);
            }
        });

        yield return ("explorer_replacement_takes_over_special_places_and_gives_them_back", "Turning on also points Windows' general folder commands, which open Home, This PC and other special places, at the app; turning off removes them and puts back a command another program had there before.", () =>
        {
            const string scratch = @"Software\NaxExplorerTests";
            string name = Guid.NewGuid().ToString("N");
            using Microsoft.Win32.RegistryKey parent = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch, writable: true);
            try
            {
                using Microsoft.Win32.RegistryKey classes = parent.CreateSubKey(name, writable: true);
                using (Microsoft.Win32.RegistryKey command = classes.CreateSubKey(@"Folder\shell\explore\command", writable: true))
                {
                    command.SetValue("", "otherfm.exe \"%1\"");
                    command.SetValue("DelegateExecute", "{11dbb47c-a525-400b-9e80-a54615a090c0}");
                }
                var replacement = new ExplorerReplacement(classes);
                Check.That(replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer"), "the first turn-on writes the entries");
                Check.That(!replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer"), "a second turn-on with everything in place wrote something");
                foreach (string verb in new[] { "open", "explore" })
                {
                    using Microsoft.Win32.RegistryKey command = classes.OpenSubKey($@"Folder\shell\{verb}\command")!;
                    Check.Equal("\"C:\\Apps\\Nax-Explorer.exe\" \"%1\"", command.GetValue("") as string, $"folder {verb} command");
                    Check.Equal("", command.GetValue("DelegateExecute") as string, $"folder {verb} runs the command");
                }
                replacement.TurnOff();
                Check.That(classes.OpenSubKey(@"Folder\shell\open") is null, "the open command the app made was left behind");
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(@"Folder\shell\explore\command")!)
                {
                    Check.Equal("otherfm.exe \"%1\"", command.GetValue("") as string, "the explore command from before");
                    Check.Equal("{11dbb47c-a525-400b-9e80-a54615a090c0}", command.GetValue("DelegateExecute") as string, "the explore handler from before");
                }
                using (Microsoft.Win32.RegistryKey verb = classes.OpenSubKey(@"Folder\shell\explore")!) Check.Equal(0, verb.ValueCount, "the app's notes on the explore key");
            }
            finally
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                using Microsoft.Win32.RegistryKey software = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: true)!;
                if (parent.SubKeyCount == 0) software.DeleteSubKey("NaxExplorerTests", throwOnMissingSubKey: false);
            }
        });

        yield return ("special_places_from_windows_go_to_the_app_or_back_to_explorer", "Home and the Win+E folder start the app, This PC opens in the app, and the Recycle Bin, Control Panel and named shell folders go to Explorer, while ordinary paths are no special places.", () =>
        {
            Check.Equal(ShellPlace.Start, ShellPlaces.Classify("::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}"), "Home");
            Check.Equal(ShellPlace.Start, ShellPlaces.Classify("\"::{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\""), "the Win+E folder of earlier versions");
            Check.Equal(ShellPlace.ThisPc, ShellPlaces.Classify("::{20d04fe0-3aea-1069-a2d8-08002b30309d}"), "This PC");
            Check.Equal(ShellPlace.ThisPc, ShellPlaces.Classify("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"), "This PC as a shell name");
            Check.Equal(ShellPlace.Explorer, ShellPlaces.Classify("::{645FF040-5081-101B-9F08-00AA002F954E}"), "Recycle Bin");
            Check.Equal(ShellPlace.Explorer, ShellPlaces.Classify(@"::{26EE0668-A00A-44D7-9371-BEB064C98683}\0"), "Control Panel");
            Check.Equal(ShellPlace.Explorer, ShellPlaces.Classify("shell:RecycleBinFolder"), "named shell folder");
            Check.Equal(ShellPlace.None, ShellPlaces.Classify(@"C:\Windows"), "ordinary folder");
            Check.Equal(ShellPlace.None, ShellPlaces.Classify(""), "nothing");
        });

        yield return ("only_places_explorer_alone_can_show_go_to_explorer_and_a_loop_stops", "A computer under Network and a folder inside a zip file go to Explorer, while folders, files such as programs, offline folders, the app's own switches and empty arguments stay with the app; opening the same place a few times is fine, but a fifth hand-off of one place within half a minute is refused as a loop.", () =>
        {
            Func<string, bool> files = path => path.Equals(@"C:\Archiv.zip", StringComparison.OrdinalIgnoreCase) || path.Equals(@"C:\Tools\setup.exe", StringComparison.OrdinalIgnoreCase);
            Func<string, bool> folders = path => files(path) || path.Equals(@"C:\", StringComparison.OrdinalIgnoreCase) || path.Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase) || path.Equals(@"\\server\share", StringComparison.OrdinalIgnoreCase);
            Check.Equal(ShellPlace.Explorer, ShellPlaces.Route(@"\\SERVER", folders, files), "a computer under Network");
            Check.Equal(ShellPlace.Explorer, ShellPlaces.Route(@"\\wsl.localhost\", folders, files), "the WSL computer");
            Check.Equal(ShellPlace.Explorer, ShellPlaces.Route(@"C:\Archiv.zip\Ordner", folders, files), "a folder inside a zip file");
            Check.Equal(ShellPlace.Explorer, ShellPlaces.Route(@"C:\Archiv.zip\a.1\b.2\Ordner", folders, files), "a folder deep inside a zip file, below folders with dots in their names");
            Check.Equal(ShellPlace.None, ShellPlaces.Route(@"C:\Tools\setup.exe", folders, files), "a program, which must never be run");
            Check.Equal(ShellPlace.None, ShellPlaces.Route(@"C:\Archiv.zip", folders, files), "a zip file itself");
            Check.Equal(ShellPlace.None, ShellPlaces.Route(@"Z:\Projekte", folders, files), "an offline network drive");
            Check.Equal(ShellPlace.None, ShellPlaces.Route("\"C:\\Windows\"", folders, files), "an ordinary folder");
            Check.Equal(ShellPlace.None, ShellPlaces.Route(@"\\server\share", folders, files), "a share");
            Check.Equal(ShellPlace.None, ShellPlaces.Route("--settings", folders, files), "a switch of the app");
            Check.Equal(ShellPlace.None, ShellPlaces.Route("", folders, files), "nothing");
            Check.Equal(ShellPlace.Start, ShellPlaces.Route("::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", folders, files), "Home");

            string state = Path.Combine(Fixtures.TempFolder("handoff"), "handoff.txt");
            var now = new DateTime(2026, 11, 4, 14, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < ShellPlaces.HandOffLimit; i++) Check.That(ShellPlaces.MayHandOff(@"\\SERVER", state, now.AddSeconds(i)), $"hand-off {i + 1} of the same place");
            Check.That(!ShellPlaces.MayHandOff(@"\\SERVER", state, now.AddSeconds(5)), "the fifth hand-off of the same place within half a minute");
            Check.That(ShellPlaces.MayHandOff("::{645FF040-5081-101B-9F08-00AA002F954E}", state, now.AddSeconds(6)), "another place");
            Check.That(ShellPlaces.MayHandOff(@"\\SERVER", state, now.AddSeconds(40)), "the same place again much later");
        });

        yield return ("an_offline_network_folder_is_asked_about_at_most_twice_at_start", "A deep folder on a share that does not answer costs one question when no parent has a dot in its name, and two, the folder and its drive, when all its parents have one, so starting the app for it never waits for the network once per folder level.", () =>
        {
            var asked = new List<string>();
            Func<string, bool> offline = path =>
            {
                asked.Add(path);
                return false;
            };
            Check.Equal(ShellPlace.None, ShellPlaces.Route(@"\\server\share\Projekte\2026\Kunden\A", offline, offline), "an offline share");
            Check.Equal(1, asked.Count, "questions for a deep folder without dotted parents on an offline share");
            asked.Clear();
            Check.Equal(ShellPlace.None, ShellPlaces.Route(@"Z:\a.1\b.2\c.3\d.4\e", offline, offline), "an offline drive with dotted folder names");
            Check.Equal(2, asked.Count, "questions for a deep folder with dotted parents on an offline drive");
            Check.Equal(@"Z:\", asked[1], "the second question is about the drive itself");
        });

        yield return ("a_handoff_marker_counts_only_for_the_same_place_and_only_for_a_moment", "The marker the app sets on the Explorer it starts is recognized for the same place even in another spelling, but not for another place, not when it is older than half a minute, and not when it is garbled.", () =>
        {
            var now = new DateTime(2026, 11, 4, 14, 0, 0, DateTimeKind.Utc);
            string marker = ShellPlaces.MarkerValue(@"\\SERVER", now);
            Check.That(ShellPlaces.IsOwnHandOff(marker, @"\\SERVER", now.AddSeconds(2)), "the same place a moment later");
            Check.That(ShellPlaces.IsOwnHandOff(marker, "\"\\\\server\\\"", now.AddSeconds(2)), "the same place in quotes, lower case and with a trailing backslash");
            Check.That(!ShellPlaces.IsOwnHandOff(marker, @"\\OTHER", now.AddSeconds(2)), "another place");
            Check.That(!ShellPlaces.IsOwnHandOff(marker, @"\\SERVER", now.AddMinutes(10)), "the same place much later, from an Explorer that lived on");
            Check.That(!ShellPlaces.IsOwnHandOff(@"\\SERVER", @"\\SERVER", now), "a marker without a time");
            Check.That(!ShellPlaces.IsOwnHandOff(null, @"\\SERVER", now), "no marker");
            Check.Equal(@"C:\", ShellPlaces.PlaceKey(@"c:\"), "a drive root keeps its backslash");
            Check.Equal(@"\\SERVER", ShellPlaces.PlaceKey(@" ""\\server\\"" "), "quotes, spaces and trailing backslashes go");
        });

        yield return ("explorer_replacement_repair_at_start_leaves_a_drive_verb_another_program_set", "The routine repair at start writes missing entries but never takes the drive verb back from a program that made it its own after the app.", () =>
        {
            const string scratch = @"Software\NaxExplorerTests";
            string name = Guid.NewGuid().ToString("N");
            using Microsoft.Win32.RegistryKey parent = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch, writable: true);
            try
            {
                using Microsoft.Win32.RegistryKey classes = parent.CreateSubKey(name, writable: true);
                var replacement = new ExplorerReplacement(classes);
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                using (Microsoft.Win32.RegistryKey drive = classes.OpenSubKey(@"Drive\shell", writable: true)!) drive.SetValue("", "otherfm");
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer", repair: true);
                using (Microsoft.Win32.RegistryKey drive = classes.OpenSubKey(@"Drive\shell")!) Check.Equal("otherfm", drive.GetValue("") as string, "the drive verb another program set");
                Check.That(replacement.IsDefault(), "folders still open in the app");
            }
            finally
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                using Microsoft.Win32.RegistryKey software = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: true)!;
                if (parent.SubKeyCount == 0) software.DeleteSubKey("NaxExplorerTests", throwOnMissingSubKey: false);
            }
        });

        yield return ("explorer_replacement_leaves_a_newer_choice_of_another_program_alone", "When another file manager takes folder opening and Win+E after the app, turning the app's setting off keeps that program's entries, removes only the app's own, and removes a command key the app created.", () =>
        {
            const string scratch = @"Software\NaxExplorerTests";
            string name = Guid.NewGuid().ToString("N");
            using Microsoft.Win32.RegistryKey parent = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch, writable: true);
            try
            {
                using Microsoft.Win32.RegistryKey classes = parent.CreateSubKey(name, writable: true);
                string open = $@"CLSID\{ExplorerReplacement.WinEClsid}\shell\opennewwindow";
                // The Win+E key existed before, but without a command.
                classes.CreateSubKey(open, writable: true).Dispose();
                var replacement = new ExplorerReplacement(classes);
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                Check.That(replacement.IsDefault(), "the app's verb is the default after turning on");
                replacement.TurnOff();
                Check.That(classes.OpenSubKey(open + @"\command") is null, "the command key the app created was left behind");
                using (Microsoft.Win32.RegistryKey key = classes.OpenSubKey(open)!) Check.Equal(0, key.ValueCount, "the app's notes on the Win+E key");

                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                // Another file manager takes over afterwards.
                using (Microsoft.Win32.RegistryKey shell = classes.OpenSubKey(@"Directory\shell", writable: true)!) shell.SetValue("", "otherfm");
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command", writable: true)!) command.SetValue("", "\"otherfm.exe\"");
                Check.That(!replacement.IsDefault(), "another program's verb was taken for the app's");
                replacement.TurnOff();
                using (Microsoft.Win32.RegistryKey shell = classes.OpenSubKey(@"Directory\shell")!)
                {
                    Check.Equal("otherfm", shell.GetValue("") as string, "the other program's folder verb");
                    Check.That(shell.OpenSubKey(ExplorerReplacement.Verb) is null, "the app's verb was left behind");
                }
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command")!) Check.Equal("\"otherfm.exe\"", command.GetValue("") as string, "the other program's Win+E command");
                using (Microsoft.Win32.RegistryKey key = classes.OpenSubKey(open)!) Check.Equal(0, key.ValueCount, "the app's notes after letting go");
            }
            finally
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                using Microsoft.Win32.RegistryKey software = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: true)!;
                if (parent.SubKeyCount == 0) software.DeleteSubKey("NaxExplorerTests", throwOnMissingSubKey: false);
            }
        });

        yield return ("folder_count_skips_links_unfetched_cloud_folders_and_waits_for_network_folders", "Counting walks into ordinary folders but not into links or cloud folders whose file list is not on this device, and a folder on a share is recognized so it is counted only on request.", () =>
        {
            Check.That(FolderStats.Enters(FileAttributes.Directory), "ordinary folder");
            Check.That(!FolderStats.Enters(FileAttributes.Directory | FileAttributes.ReparsePoint), "junction or link");
            Check.That(!FolderStats.Enters(FileAttributes.Directory | CloudFiles.RecallOnOpen), "cloud folder not fetched yet");
            Check.That(FolderStats.IsNetworkPath(@"\\server\share\Projekte"), "folder on a share");
            Check.That(!FolderStats.IsNetworkPath(Environment.SystemDirectory), "folder on the system drive");
            Check.That(!FolderStats.IsNetworkPath(@"\\?\C:\Windows"), "long local path");
            Check.That(FolderStats.IsNetworkPath(@"\\?\UNC\server\share\Projekte"), "long form of a share");
            Check.That(FolderStats.IsNetworkPath(@"\\.\UNC\server\share\Projekte"), "device form of a share");
            Check.That(CloudFiles.IsUnfetchedFolder(FileAttributes.Directory | CloudFiles.RecallOnOpen), "cloud folder whose list is not fetched");
            Check.That(!CloudFiles.IsUnfetchedFolder(FileAttributes.Directory), "ordinary folder seen as unfetched");
            Check.That(!CloudFiles.IsUnfetchedFolder(CloudFiles.RecallOnDataAccess), "a file seen as an unfetched folder");
        });

        yield return ("explorer_replacement_recognizes_an_older_build_and_keeps_a_later_win_e_choice", "Win+E entries written by an earlier build without the app's note are still given back on turning off; turning on again, as the start-up repair does, finishes a takeover that stopped halfway but never overwrites a Win+E command another program wrote later, even its own earlier one.", () =>
        {
            const string scratch = @"Software\NaxExplorerTests";
            string name = Guid.NewGuid().ToString("N");
            using Microsoft.Win32.RegistryKey parent = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch, writable: true);
            try
            {
                using Microsoft.Win32.RegistryKey classes = parent.CreateSubKey(name, writable: true);
                string open = $@"CLSID\{ExplorerReplacement.WinEClsid}\shell\opennewwindow";
                using (Microsoft.Win32.RegistryKey command = classes.CreateSubKey(open + @"\command", writable: true)) command.SetValue("", "other.exe");
                var replacement = new ExplorerReplacement(classes);
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                // An earlier build wrote the same entries but no note of its own command.
                using (Microsoft.Win32.RegistryKey key = classes.OpenSubKey(open, writable: true)!) key.DeleteValue("NaxExplorerCommand");
                replacement.TurnOff();
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command")!)
                {
                    Check.Equal("other.exe", command.GetValue("") as string, "the Win+E command from before the earlier build");
                    Check.Equal(null, command.GetValue("DelegateExecute"), "a DelegateExecute that was not there before");
                }

                // A takeover that stopped after the app's notes but before the command was written is finished by the
                // next turn-on, as the start-up repair does.
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command", writable: true)!) command.SetValue("", "other.exe");
                // After a finished takeover, the earlier program taking Win+E back with its old command keeps it.
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command")!) Check.Equal("other.exe", command.GetValue("") as string, "the earlier program's reclaim of Win+E");
                // A takeover that stopped before it finished leaves no "finished" note and is completed.
                using (Microsoft.Win32.RegistryKey key = classes.OpenSubKey(open, writable: true)!) key.DeleteValue("NaxExplorerFinished");
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command")!) Check.Equal("\"C:\\Apps\\Nax-Explorer.exe\"", command.GetValue("") as string, "the interrupted takeover was finished");

                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command", writable: true)!) command.SetValue("", "\"otherfm.exe\"");
                replacement.TurnOn(@"D:\Moved\Nax-Explorer.exe", "Open in Nax Explorer");
                Check.Equal(@"D:\Moved\Nax-Explorer.exe", replacement.RegisteredProgram(), "the folder verb follows the moved app");
                using (Microsoft.Win32.RegistryKey command = classes.OpenSubKey(open + @"\command")!) Check.Equal("\"otherfm.exe\"", command.GetValue("") as string, "the later Win+E choice of another program");
            }
            finally
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                using Microsoft.Win32.RegistryKey software = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: true)!;
                if (parent.SubKeyCount == 0) software.DeleteSubKey("NaxExplorerTests", throwOnMissingSubKey: false);
            }
        });

        yield return ("cloud_folders_of_this_user_are_found_with_names_people_know", "Sync folders registered for this user are found with the app's name, two folders of one app are told apart by folder name, other users' folders are left out, and a path inside a cloud folder is recognized.", () =>
        {
            const string scratch = @"Software\NaxExplorerTests";
            string name = Guid.NewGuid().ToString("N");
            using Microsoft.Win32.RegistryKey parent = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch, writable: true);
            try
            {
                using Microsoft.Win32.RegistryKey roots = parent.CreateSubKey(name, writable: true);
                void Root(string id, string sid, string path, string? display = null)
                {
                    using Microsoft.Win32.RegistryKey root = roots.CreateSubKey(id, writable: true);
                    if (display is not null) root.SetValue("DisplayNameResource", display);
                    using Microsoft.Win32.RegistryKey users = root.CreateSubKey("UserSyncRoots", writable: true);
                    users.SetValue(sid, path);
                }
                Root("Nextcloud!S-1-5-21-1!account1!1", "S-1-5-21-1", @"D:\NC1\");
                Root("Nextcloud!S-1-5-21-1!account2!1", "S-1-5-21-1", @"E:\NC2");
                Root("OneDrive!S-1-5-21-1!Personal|1", "S-1-5-21-1", @"C:\Users\Anna\OneDrive", @"@C:\Windows\onedrive.dll,-1");
                Root("Acme!S-1-5-21-1!x", "S-1-5-21-1", @"C:\Acme", "Acme Cloud");
                Root("ProtonDrive!S-1-5-21-2!x", "S-1-5-21-2", @"C:\Users\Ben\Proton Drive");
                List<CloudFolder> found = CloudFolders.FromSyncRoots(roots, "S-1-5-21-1");
                Check.Sequence(["Acme Cloud", "Nextcloud (NC1)", "Nextcloud (NC2)", "OneDrive"], found.Select(f => f.Name));
                Check.Equal(@"D:\NC1", found[1].Path, "path without its last backslash");
                Check.That(CloudFolders.Contains(found, @"D:\NC1\Steuer\2024.pdf"), "a file inside a cloud folder");
                Check.That(!CloudFolders.Contains(found, @"D:\NC10\x.txt"), "a folder that only starts with the same name");
                Check.Equal("Proton Drive", CloudFolders.NameOf("ProtonDrive", null), "Proton Drive name");
            }
            finally
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                using Microsoft.Win32.RegistryKey software = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: true)!;
                if (parent.SubKeyCount == 0) software.DeleteSubKey("NaxExplorerTests", throwOnMissingSubKey: false);
            }
        });

        yield return ("cloud_status_tells_online_only_from_on_device_and_always_kept", "A cloud file shows whether it is only online, on this device or always kept there; files outside cloud folders and folders whose contents are unknown show nothing.", () =>
        {
            Check.Equal(CloudState.OnlineOnly, CloudFiles.StateOf(CloudFiles.RecallOnDataAccess | FileAttributes.Archive, inCloudFolder: true), "placeholder");
            Check.Equal(CloudState.OnDevice, CloudFiles.StateOf(FileAttributes.Archive, inCloudFolder: true), "downloaded file");
            Check.Equal(CloudState.AlwaysOnDevice, CloudFiles.StateOf(CloudFiles.Pinned | FileAttributes.Archive, inCloudFolder: true), "pinned file");
            Check.Equal(CloudState.None, CloudFiles.StateOf(FileAttributes.Archive, inCloudFolder: false), "ordinary file");
            Check.Equal(CloudState.None, CloudFiles.StateOf(FileAttributes.Directory, inCloudFolder: true), "folder");
            Check.Equal(CloudState.AlwaysOnDevice, CloudFiles.StateOf(FileAttributes.Directory | CloudFiles.Pinned, inCloudFolder: true), "pinned folder");
        });

        yield return ("folder_totals_tell_how_much_lives_only_online", "Counting a folder in a cloud folder also tells how many files and bytes live only online, without opening any file.", () =>
        {
            string root = Fixtures.TempFolder("onlinetotals");
            string online = Fixtures.Write(root, "online.txt", new string('o', 10));
            Fixtures.Write(root, "local.txt", new string('l', 20));
            File.SetAttributes(online, FileAttributes.Offline);
            try
            {
                FolderTotals totals = FolderStats.Measure([root], CancellationToken.None);
                Check.Equal(2L, totals.Files, "files");
                Check.Equal(30L, totals.Bytes, "bytes");
                Check.Equal(1L, totals.OnlineFiles, "files only online");
                Check.Equal(10L, totals.OnlineBytes, "bytes only online");
            }
            finally
            {
                File.SetAttributes(online, FileAttributes.Normal);
            }
        });

        yield return ("folders_sort_by_the_name_windows_shows", "Sorting by name uses the shown names, so \"Bilder\" (Pictures) comes before \"Dokumente\" (Documents), as in Explorer.", () =>
        {
            var entries = new List<FileEntry>
            {
                new() { Name = "Pictures", DisplayName = "Bilder", FullPath = @"C:\home\Pictures", Kind = EntryKind.Folder },
                new() { Name = "Documents", DisplayName = "Dokumente", FullPath = @"C:\home\Documents", Kind = EntryKind.Folder },
                new() { Name = "Contacts", DisplayName = "Kontakte", FullPath = @"C:\home\Contacts", Kind = EntryKind.Folder },
            };
            EntrySorter.Sort(entries, SortColumn.Name, descending: false);
            Check.Sequence(["Bilder", "Dokumente", "Kontakte"], entries.Select(e => e.DisplayName));
        });

        yield return ("explorer_view_is_taken_over_only_without_a_settings_file", "Without a settings file the app shows hidden items and extensions as Explorer does; a saved file, even one from an earlier version, keeps the app's own choices.", () =>
        {
            string folder = Fixtures.TempFolder("settings");
            string path = Path.Combine(folder, "settings.json");
            ExplorerView explorer = ExplorerView.From(hidden: 1, superHidden: 0, hideExtensions: 0);
            AppSettings first = AppSettings.Load(path, explorer);
            Check.That(first.ShowHidden && first.ShowExtensions && !first.ShowProtectedFiles, "Explorer's view was not taken over");
            first.ShowHidden = false;
            first.Save(path);
            Check.That(!AppSettings.Load(path, explorer).ShowHidden, "the app's own choice was overwritten by Explorer's");
            string older = Fixtures.Write(folder, "older.json", "{ \"ShowHidden\": false, \"ShowExtensions\": false, \"ViewVersion\": 1 }");
            AppSettings kept = AppSettings.Load(older, explorer);
            Check.That(!kept.ShowHidden && !kept.ShowExtensions, "a settings file from an earlier version lost its own choices");
        });

        yield return ("start_folder_setting_names_the_place_a_pane_opens", "An empty setting restores the last session; home, This PC and an existing folder open there; a vanished folder restores the last session.", () =>
        {
            string folder = Fixtures.TempFolder("start");
            const string home = @"C:\home";
            Check.Equal(null, AppSettings.StartPlace("", home));
            Check.Equal(home, AppSettings.StartPlace(AppSettings.StartHome, home));
            Check.Equal(PathTools.ThisPc, AppSettings.StartPlace(PathTools.ThisPc, home));
            Check.Equal(folder, AppSettings.StartPlace(folder, home));
            Check.Equal(null, AppSettings.StartPlace(Path.Combine(folder, "gone"), home));
        });

        yield return ("damaged_settings_file_gives_default_settings", "A settings file that is not valid JSON never stops the app; the defaults are used.", () =>
        {
            string folder = Fixtures.TempFolder("settings");
            string path = Fixtures.Write(folder, "settings.json", "{ this is not json");
            AppSettings settings = AppSettings.Load(path, ExplorerView.From(null, null, 0));
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
