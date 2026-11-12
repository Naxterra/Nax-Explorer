using NaxExplorer.Core;

namespace NaxExplorer.Tests;

internal static class RenameTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("new_folder_creation_handles_long_parent_paths", "A new folder in a path longer than MAX_PATH uses the same naming and creation behavior as shallow folders.", () =>
        {
            string root = Fixtures.TempFolder("long-new-folder");
            string parent = Path.Combine(root, new string('a', 100), new string('b', 100), new string('c', 100));
            Directory.CreateDirectory(parent);
            string created = NewItemCreator.Create(parent, "New folder", true);
            Check.That(Directory.Exists(created), "long-path folder missing");
        });
        yield return ("rename_refocus_keeps_typed_prefix_and_does_not_reselect", "Only the initial focus selects the original name; later focus recovery keeps the full draft instead of replacing its first letter.", () =>
        {
            var edit = new RenameEditState();
            edit.Begin("New folder", isFile: false);
            Check.That(edit.TakeInitialSelection(out int length) && length == 10, "initial name selection missing");
            edit.UpdateText("E");
            Check.That(edit.IsActive && !edit.TakeInitialSelection(out _), "refocus would select over the typed prefix");
            edit.UpdateText(edit.Text + "xpendables");
            Check.Equal("Expendables", edit.Text);
            Check.That(edit.IsActive, "typing prematurely ended rename");
        });
        yield return ("rename_cancellation_rejects_late_updates_and_next_edit_starts_fresh", "Cancellation prevents delayed editor callbacks from changing a draft; the next rename gets its own initial selection.", () =>
        {
            var edit = new RenameEditState();
            edit.Begin("first", false);
            edit.UpdateText("draft"); edit.End(); edit.UpdateText("late callback");
            Check.Equal("draft", edit.Text);
            Check.That(!edit.IsActive && !edit.TakeInitialSelection(out _), "cancelled editor still accepted focus");
            edit.Begin("second", false);
            Check.Equal("second", edit.Text);
            Check.That(edit.TakeInitialSelection(out int length) && length == 6, "next rename inherited old focus state");
        });
        yield return ("rename_initial_selection_distinguishes_files_and_dotted_folders", "A file's extension stays outside the initial selection, while dotted folder names and dotfiles are selected in full.", () =>
        {
            var edit = new RenameEditState();
            foreach (var (name, isFile, expected) in new[] { ("report.txt", true, 6), ("Folder.Name", false, 11), (".config", true, 7) })
            {
                edit.Begin(name, isFile);
                Check.That(edit.TakeInitialSelection(out int length) && length == expected, "wrong selection for " + name);
                edit.End();
            }
        });
        yield return ("new_folder_requests_reserve_distinct_names_without_reusing_existing_content", "Concurrent folder creation requests each get their own directory and never reuse a pre-existing folder or file.", () =>
        {
            string root = Fixtures.TempFolder("new-folder-race");
            string existing = Path.Combine(root, "New folder"); Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "original");
            File.WriteAllText(Path.Combine(root, "New folder (2)"), "file collision");
            string[] created = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => NewItemCreator.Create(root, "New folder", true)))).GetAwaiter().GetResult();
            Check.Equal(8, created.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Check.That(created.All(Directory.Exists) && created.All(path => Directory.GetFileSystemEntries(path).Length == 0), "creation reused existing content");
            Check.Equal("original", File.ReadAllText(Path.Combine(existing, "keep.txt")));
            Check.Equal("file collision", File.ReadAllText(Path.Combine(root, "New folder (2)")));
        });
        yield return ("new_item_creation_reports_missing_parent_and_keeps_existing_text", "New text files use unused names without overwriting existing text, and an absent parent reports failure rather than silently creating a different path.", () =>
        {
            string root = Fixtures.TempFolder("new-item-errors");
            string first = NewItemCreator.Create(root, "Note.txt", false); File.WriteAllText(first, "keep");
            string second = NewItemCreator.Create(root, "Note.txt", false);
            Check.Equal("Note (2).txt", Path.GetFileName(second));
            Check.Equal("keep", File.ReadAllText(first));
            Check.Equal(0L, new FileInfo(second).Length);
            bool failed = false;
            try { NewItemCreator.Create(Path.Combine(root, "missing"), "Folder", true); }
            catch (IOException) { failed = true; }
            Check.That(failed && !Directory.Exists(Path.Combine(root, "missing")), "failed creation manufactured a parent directory");
        });
    }
}
