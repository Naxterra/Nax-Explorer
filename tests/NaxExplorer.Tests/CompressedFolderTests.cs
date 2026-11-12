using NaxExplorer.Core;
using NaxExplorer.Shell;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NaxExplorer.Tests;

internal static class CompressedFolderTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("zip_folder_drag_reaches_extraction_engine", "An explicit ZIP directory with spaces uses the same drag decision as the panes, extracts its complete subtree on copy, and removes it only for a Shift move.", () =>
        {
            string root = Fixtures.TempFolder("zip-folder-drag");
            string zip = Path.Combine(root, "Game-Alternative-ISO-Version.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntry("Game ISO/");
                foreach ((string name, string payload) in new[] { ("Game ISO/Game - Gorgon's Alliance.nfo", "game information"), ("Game ISO/Game.BIN", "generated disc payload"), ("Game ISO/Game.CUE", "generated cue") })
                {
                    using var text = new StreamWriter(archive.CreateEntry(name).Open());
                    text.Write(payload);
                }
            }
            var listing = ShellFolderReader.ReadAsync(CompressedFolders.Location(zip), true, default).GetAwaiter().GetResult();
            string folder = listing.Entries.Single().FullPath;
            string target = Directory.CreateDirectory(Path.Combine(root, "Copied")).FullName;
            var drag = new FileDragSession([folder]);
            drag.Hover(target, false, false);
            Check.That(drag.Release(false, false) is { Move: false }, "pane drag never reached extraction");
            RunDrop(drag);
            Check.Equal("generated disc payload", File.ReadAllText(Path.Combine(target, @"Game ISO\Game.BIN")));
            Check.Equal("game information", File.ReadAllText(Path.Combine(target, @"Game ISO\Game - Gorgon's Alliance.nfo")));
            Check.That(CompressedFolders.EntryExists(folder), "plain drag removed the source ZIP folder");
            string moved = Directory.CreateDirectory(Path.Combine(root, "Moved")).FullName;
            drag = new FileDragSession([folder]);
            drag.Hover(moved, true, false);
            drag.Hover(moved, false, true);
            Check.That(drag.Release(false, true) is { Move: true }, "Shift release did not choose move");
            RunDrop(drag);
            Check.Equal("generated disc payload", File.ReadAllText(Path.Combine(moved, @"Game ISO\Game.BIN")));
            Check.Equal("generated cue", File.ReadAllText(Path.Combine(moved, @"Game ISO\Game.CUE")));
            using (var archive = ZipFile.OpenRead(zip))
                Check.That(archive.Entries.All(e => e.FullName.EndsWith('/')), "Shift drag left moved file payload in the archive");

            static void RunDrop(FileDragSession drag)
            {
                var drop = drag.Choice!.Value;
                var progress = new OperationProgress();
                ShellActions.TransferAsync(TransferPlan.Plan(drag.Sources, drop.Target, drop.Move, null), drop.Move, IntPtr.Zero, progress).GetAwaiter().GetResult();
                Check.That(!progress.IsCancelled && progress.Failures.Count == 0, "archive drag transfer failed");
            }
        });
        yield return ("zip_open_routes_directly_and_restores_previous_handler", "Folder replacement sends the Windows ZIP handler directly to Nax Explorer, restores its prior command and DelegateExecute on disable, and preserves newer app choices.", () =>
        {
            string scratch = @"Software\NaxExplorerTests\" + Guid.NewGuid().ToString("N");
            using var classes = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch, writable: true);
            try
            {
                const string key = @"CompressedFolder\shell\open\command";
                using (var command = classes.CreateSubKey(key))
                {
                    command.SetValue("", @"C:\Windows\explorer.exe /idlist,%I,%L");
                    command.SetValue("DelegateExecute", "original-delegate");
                }
                using (var winrar = classes.CreateSubKey(@"WinRAR.ZIP\shell\open\command")) winrar.SetValue("", "winrar.exe %1");
                var replacement = new ExplorerReplacement(classes);
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                using (var command = classes.OpenSubKey(key)!)
                {
                    Check.Equal("\"C:\\Apps\\Nax-Explorer.exe\" \"%1\"", command.GetValue("") as string);
                    Check.Equal("", command.GetValue("DelegateExecute") as string);
                }
                Check.That(!replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer", repair: true), "unchanged integration was rewritten");
                replacement.TurnOff();
                using (var command = classes.OpenSubKey(key)!)
                {
                    Check.Equal(@"C:\Windows\explorer.exe /idlist,%I,%L", command.GetValue("") as string);
                    Check.Equal("original-delegate", command.GetValue("DelegateExecute") as string);
                }
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer");
                using (var command = classes.OpenSubKey(key, writable: true)!) command.SetValue("", "new-handler.exe %1");
                replacement.TurnOn(@"C:\Apps\Nax-Explorer.exe", "Open in Nax Explorer", repair: true);
                replacement.TurnOff();
                using (var command = classes.OpenSubKey(key)!) Check.Equal("new-handler.exe %1", command.GetValue("") as string);
                using (var winrar = classes.OpenSubKey(@"WinRAR.ZIP\shell\open\command")!) Check.Equal("winrar.exe %1", winrar.GetValue("") as string);
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(scratch, false); }
        });
        yield return ("zip_native_copy_and_move_extract_contents", "Copying a ZIP file and nested directory extracts their payloads; moving removes only the successfully transferred entries.", () =>
        {
            string root = Fixtures.TempFolder("zip-transfer");
            string zip = Path.Combine(root, "Example.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var text = new StreamWriter(archive.CreateEntry("root.txt").Open())) text.Write("root payload");
                using (var text = new StreamWriter(archive.CreateEntry("Nested/readme.txt").Open())) text.Write("nested payload");
            }
            string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            string[] sources = [CompressedFolders.Location(zip + @"\root.txt"), CompressedFolders.Location(zip + @"\Nested")];
            var progress = new OperationProgress();
            ShellActions.TransferAsync(TransferPlan.Plan(sources, target, false, null), false, IntPtr.Zero, progress).GetAwaiter().GetResult();
            Check.Equal(0, progress.Failures.Count);
            Check.That(!progress.IsCancelled, "ZIP copy cancelled");
            Check.Equal("root payload", File.ReadAllText(Path.Combine(target, "root.txt")));
            Check.Equal("nested payload", File.ReadAllText(Path.Combine(target, @"Nested\readme.txt")));
            using (var archive = ZipFile.OpenRead(zip)) Check.Equal(2, archive.Entries.Count);
            string moved = Directory.CreateDirectory(Path.Combine(root, "moved")).FullName;
            progress = new OperationProgress();
            var cut = new CutPasteCompletion(sources, 1);
            Check.That(!cut.AllMoved(true), "cut ZIP selection cleared before moving");
            ShellActions.TransferAsync(TransferPlan.Plan(sources, moved, true, null), true, IntPtr.Zero, progress).GetAwaiter().GetResult();
            Check.Equal(0, progress.Failures.Count);
            Check.That(!progress.IsCancelled, "ZIP move cancelled");
            Check.Equal("root payload", File.ReadAllText(Path.Combine(moved, "root.txt")));
            Check.Equal("nested payload", File.ReadAllText(Path.Combine(moved, @"Nested\readme.txt")));
            using (var archive = ZipFile.OpenRead(zip)) Check.That(archive.Entries.All(e => e.FullName.EndsWith('/')), "moved files remain in ZIP");
            Check.That(cut.AllMoved(true), "completed ZIP cut did not clear");
        });
        foreach (ConflictChoice choice in Enum.GetValues<ConflictChoice>())
        {
            yield return ($"zip_conflicts_{choice}", "ZIP folder conflicts merge safely for Replace and Skip, or keep a separate folder for KeepBoth; skipped move sources remain available.", () =>
            {
                string root = Fixtures.TempFolder("zip-conflict");
                string zip = CreateZip(root);
                string folder = CompressedFolders.Location(zip + @"\Nested");
                string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
                Fixtures.Write(target, @"Nested\readme.txt", "original");
                Fixtures.Write(target, @"Nested\unrelated.txt", "leave alone");
                var conflicts = TransferPlan.FindConflicts([folder], target);
                Check.Equal(1, conflicts.FilesReplaced);
                var batches = TransferPlan.Plan([folder], target, true, choice, conflicts.Items);
                var progress = new OperationProgress();
                ShellActions.TransferAsync(batches, true, IntPtr.Zero, progress).GetAwaiter().GetResult();
                Check.Equal(0, progress.Failures.Count);
                Check.That(!progress.IsCancelled, "ZIP conflict transfer cancelled");
                string[] folders = Directory.GetDirectories(target);
                Check.Equal(choice == ConflictChoice.KeepBoth ? 2 : 1, folders.Length, "destination folder count: " + string.Join(", ", folders));
                string name = choice == ConflictChoice.KeepBoth ? Path.GetFileName(folders.Single(p => Path.GetFileName(p) != "Nested")) : "Nested";
                if (choice == ConflictChoice.KeepBoth) Check.Equal("original", File.ReadAllText(Path.Combine(target, @"Nested\readme.txt")));
                Check.Equal(choice == ConflictChoice.Skip ? "original" : "nested payload", File.ReadAllText(Path.Combine(target, name, "readme.txt")));
                Check.Equal("new payload", File.ReadAllText(Path.Combine(target, name, "new.txt")));
                Check.Equal("leave alone", File.ReadAllText(Path.Combine(target, @"Nested\unrelated.txt")));
                using var archive = ZipFile.OpenRead(zip);
                Check.That(archive.GetEntry("root.txt") is not null, "unselected ZIP entry was removed");
                Check.Equal(choice == ConflictChoice.Skip, archive.GetEntry("Nested/readme.txt") is not null);
                Check.Equal(choice != ConflictChoice.Skip, new CutPasteCompletion([folder], 1).AllMoved(true));
            });
        }
        yield return ("zip_cancelled_move_retains_archive", "Cancelling before extraction leaves the source archive and existing destination data unchanged.", () =>
        {
            string root = Fixtures.TempFolder("zip-cancel");
            string zip = CreateZip(root);
            byte[] before = File.ReadAllBytes(zip);
            string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            var progress = new OperationProgress();
            progress.Cancel();
            string source = CompressedFolders.Location(zip + @"\Nested");
            ShellActions.TransferAsync(TransferPlan.Plan([source], target, true, null), true, IntPtr.Zero, progress).GetAwaiter().GetResult();
            Check.Sequence(before, File.ReadAllBytes(zip));
            Check.Equal(0, Directory.GetFileSystemEntries(target).Length);
            Check.That(!new CutPasteCompletion([source], 1).AllMoved(false), "cancelled cut was cleared");
            File.Move(zip, zip + ".bak");
            Check.That(!new CutPasteCompletion([source], 1).AllMoved(true), "inaccessible ZIP mistaken for completed cut");
            File.WriteAllText(zip, "invalid archive");
            Check.That(!new CutPasteCompletion([source], 1).AllMoved(true), "corrupt ZIP mistaken for completed cut");
        });
        yield return ("zip_clipboard_shell_identities_round_trip", "The standard shell clipboard payload retains ZIP file and directory identities without extracting their contents or publishing invalid filesystem paths.", () =>
        {
            string root = Fixtures.TempFolder("zip-clipboard");
            string zip = CreateZip(root);
            string[] sources = [CompressedFolders.Location(zip + @"\root.txt"), CompressedFolders.Location(zip + @"\Nested")];
            byte[] before = File.ReadAllBytes(zip);
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    byte[] encoded = ShellClipboardIds.Encode(sources);
                    IntPtr buffer = Marshal.AllocHGlobal(encoded.Length);
                    try
                    {
                        Marshal.Copy(encoded, 0, buffer, encoded.Length);
                        Check.Sequence(sources, ShellClipboardIds.Decode(buffer, encoded.Length)!);
                        Check.That(ShellClipboardIds.Decode(buffer, 8) is null, "truncated header accepted");
                        Marshal.WriteInt32(buffer, 4, int.MaxValue);
                        Check.That(ShellClipboardIds.Decode(buffer, encoded.Length) is null, "invalid PIDL offset accepted");
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error is not null) throw error;
            Check.Sequence(before, File.ReadAllBytes(zip));
            Check.Equal(1, Directory.GetFileSystemEntries(root).Length);
            Check.That(!CompressedFolders.IsEntry(PathTools.ShellPlace(root + @"\not.zip.txt\child")), "non-ZIP shell item accepted");
            Check.That(!CompressedFolders.IsEntry("shell:RecycleBinFolder"), "Recycle Bin accepted as ZIP");
        });
        yield return ("zip_move_failure_keeps_source_payload", "A ZIP move to an invalid destination reports failure while leaving every source entry intact.", () =>
        {
            string root = Fixtures.TempFolder("zip-failed-move");
            string zip = CreateZip(root);
            byte[] before = File.ReadAllBytes(zip);
            string target = Fixtures.Write(root, "not-a-folder.txt", "keep target");
            var progress = new OperationProgress();
            bool failed = false;
            try
            {
                ShellActions.TransferAsync([new TransferBatch([CompressedFolders.Location(zip + @"\root.txt")], target, OperationFlags.ForTransfer(false))], true, IntPtr.Zero, progress).GetAwaiter().GetResult();
            }
            catch (COMException) { failed = true; }
            Check.That(failed || progress.Failures.Count > 0 || progress.IsCancelled, "failed move reported success");
            Check.Sequence(before, File.ReadAllBytes(zip));
            Check.Equal("keep target", File.ReadAllText(target));
        });
        yield return ("zip_move_cancellation_at_copy_callback_keeps_source", "A cancellation received from the live copy engine before a ZIP file transfers leaves the archive entry available for retry.", () =>
        {
            string root = Fixtures.TempFolder("zip-callback-cancel");
            string zip = CreateZip(root);
            byte[] before = File.ReadAllBytes(zip);
            string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            var progress = new OperationProgress();
            progress.Changed += (_, _) => progress.Cancel();
            ShellActions.TransferAsync([new TransferBatch([CompressedFolders.Location(zip + @"\root.txt")], target, OperationFlags.ForTransfer(false))], true, IntPtr.Zero, progress).GetAwaiter().GetResult();
            Check.That(progress.IsCancelled, "no cancellation callback");
            Check.Sequence(before, File.ReadAllBytes(zip));
            Check.That(CompressedFolders.EntryExists(CompressedFolders.Location(zip + @"\root.txt")), "cancelled move removed source");
        });
        yield return ("zip_shell_navigation_lists_root_nested_folder_and_parent", "A real ZIP resolves as a folder despite its stream attribute; root and nested entries can be listed with shell identities, and Up returns to the archive.", () =>
        {
            string root = Fixtures.TempFolder("zip-navigation");
            string zip = Path.Combine(root, "Example.ZIP");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var text = new StreamWriter(archive.CreateEntry("root.txt").Open())) text.Write("root payload");
                using (var text = new StreamWriter(archive.CreateEntry("Nested/readme.txt").Open())) text.Write("nested payload");
            }
            Check.That(CompressedFolders.IsZip(zip) && !CompressedFolders.IsZip(zip + ".txt"), "ZIP classification");
            string? location = ShellFolderReader.ResolveAsync(zip).GetAwaiter().GetResult();
            Check.That(location is not null && PathTools.IsShellPlace(location), "ZIP did not resolve as a virtual folder");
            var listing = ShellFolderReader.ReadAsync(location!, true, default).GetAwaiter().GetResult();
            Check.Equal(2, listing.Entries.Count);
            FileEntry nested = listing.Entries.Single(entry => entry.Kind == EntryKind.Folder);
            Check.That(PathTools.IsShellPlace(nested.FullPath), "archive child mistaken for a physical directory");
            var child = ShellFolderReader.ReadAsync(nested.FullPath, true, default).GetAwaiter().GetResult();
            Check.Equal(1, child.Entries.Count);
            Check.That(child.Entries.Single().Name.StartsWith("readme", StringComparison.OrdinalIgnoreCase), "nested file missing");
            Check.That(PathTools.SameLocation(location!, child.Parent), "Up left the archive unexpectedly");
            Check.That(PathTools.SameLocation(root, listing.Parent), "archive parent is not its real folder");
            Check.That(ShellFolderReader.ResolveAsync(Path.Combine(root, "missing.zip")).GetAwaiter().GetResult() is null, "missing archive resolved");
        });
    }

    private static string CreateZip(string root)
    {
        string zip = Path.Combine(root, "Example.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach ((string name, string value) in new[] { ("root.txt", "root payload"), ("Nested/readme.txt", "nested payload"), ("Nested/new.txt", "new payload") })
        {
            using var text = new StreamWriter(archive.CreateEntry(name).Open());
            text.Write(value);
        }
        return zip;
    }
}
