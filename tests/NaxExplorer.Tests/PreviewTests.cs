using NaxExplorer.Core;

namespace NaxExplorer.Tests;

internal static class PreviewTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static FileEntry Entry(string path, EntryKind kind = EntryKind.File, long size = 10, int revision = 0, FileAttributes attributes = 0) => new()
    {
        Name = Path.GetFileName(path.TrimEnd('\\')), FullPath = path, Kind = kind, Size = size,
        ModifiedUtc = Stamp.AddSeconds(revision), CreatedUtc = Stamp, Attributes = attributes,
    };
    private static PreviewRequest Request(params FileEntry[] entries) => new(entries.Select(e => new PreviewEntry(e, e.DisplayName)), @"C:\Docs", 600, 1);
    private static FolderChanges Changes(FileEntry entry, bool removed = false) => removed ? new([entry], [], []) : new([], [], [entry]);

    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("preview_selection_identity_retires_old_reads_before_debounce", "A new selection or pane context retires the old read immediately, while repeated notifications for the same selection leave it running.", () =>
        {
            var request = Request(Entry(@"C:\Docs\a.pdf"));
            Check.That(request.SameSelection([@"c:\docs\a.pdf"], 1, @"C:\Docs"), "same file would be cancelled");
            Check.That(!request.SameSelection([@"C:\Docs\b.pdf"], 1, @"C:\Docs"), "old file would still paint before the timer");
            Check.That(!request.SameSelection([], 0, @"C:\Docs"), "clearing selection did not retire the read");
            Check.That(!request.SameSelection([@"C:\Docs\a.pdf"], 1, @"D:\Other"), "changing pane context did not retire the read");
            var multi = Request(Entry(@"C:\Docs\a.pdf"), Entry(@"C:\Docs\b.pdf"));
            Check.That(multi.SameSelection([@"C:\Docs\b.pdf", @"C:\Docs\a.pdf"], 2, @"C:\Docs"), "order-only change cancelled the selection");
        });

        yield return ("unchanged_preview_requests_keep_pending_and_loaded_content", "Repeated selection notifications and replacement row objects for an unchanged file do not restart the read or rebuild the preview.", () =>
        {
            var state = new PreviewRefreshState();
            long first = state.Begin(Request(Entry(@"C:\Docs\report.pdf")))!.Value;
            Check.That(state.Begin(Request(Entry(@"C:\Docs\report.pdf"))) is null, "duplicate cancelled a pending read");
            Check.That(!state.Complete(first), "unchanged request scheduled a follow-up");
            for (int i = 0; i < 500; i++)
                Check.That(state.Begin(Request(Entry(@"C:\Docs\report.pdf"))) is null, "unchanged content was rebuilt");
        });

        yield return ("preview_ignores_siblings_other_panes_and_path_prefixes", "A changing sibling file, another pane's folder and a similar path prefix do not invalidate the selected PDF.", () =>
        {
            var state = new PreviewRefreshState();
            var request = Request(Entry(@"C:\Docs\report.pdf"));
            state.Complete(state.Begin(request)!.Value);
            foreach (string path in new[] { @"C:\Docs\download.tmp", @"D:\Downloads\busy.log", @"C:\Docs2\report.pdf", @"C:\Docs\subfolder" })
                Check.Equal(PreviewRefreshAction.None, state.Invalidate(Path.GetDirectoryName(path)!, Changes(Entry(path))));
            Check.That(state.Begin(request) is null, "unrelated changes dirtied the preview");
        });

        yield return ("preview_reloads_real_file_changes_and_removed_ancestors", "Real listing diffs for the selected file refresh it, while deletion of its containing folder also invalidates it.", () =>
        {
            string folder = Fixtures.TempFolder("preview-refresh");
            string path = Fixtures.Write(folder, "selected.txt", "before");
            var before = DirectoryReader.Read(folder, new(true));
            var state = new PreviewRefreshState();
            state.Complete(state.Begin(Request(before.Single()))!.Value);
            File.WriteAllText(path, "a changed and longer preview");
            var after = DirectoryReader.Read(folder, new(true));
            var changes = FolderChanges.Compute(before, after);
            Check.Equal(PreviewRefreshAction.Reload, state.Invalidate(folder, changes));
            state.Cancel();
            long refreshed = state.Begin(Request(after.Single()))!.Value;
            Check.That(!state.Complete(refreshed), "file change kept refreshing after completion");
            Check.Equal(PreviewRefreshAction.Reload, state.Invalidate(Path.GetDirectoryName(folder)!, Changes(Entry(folder, EntryKind.Folder), removed: true)));
        });

        yield return ("preview_totals_refresh_only_for_selected_trees", "Folder totals notice nested changes but ignore adjacent folders; multi-selection totals ignore unselected sibling files.", () =>
        {
            var folder = Request(Entry(@"C:\Docs\selected", EntryKind.Folder));
            Check.That(folder.AffectedBy(@"C:\Docs\selected\nested", Changes(Entry(@"C:\Docs\selected\nested\new.txt"))), "nested total stayed stale");
            Check.That(!folder.AffectedBy(@"C:\Docs\selected-other", Changes(Entry(@"C:\Docs\selected-other\new.txt"))), "prefix matched an adjacent folder");
            var multi = Request(Entry(@"C:\Docs\a.txt"), Entry(@"C:\Docs\selected", EntryKind.Folder));
            Check.That(!multi.AffectedBy(@"C:\Docs", Changes(Entry(@"C:\Docs\b.txt"))), "unselected sibling refreshed totals");
            Check.That(multi.AffectedBy(@"C:\Docs", Changes(Entry(@"C:\Docs\a.txt", size: 100))), "selected file size stayed stale");
            var overview = new PreviewRequest([], @"C:\Docs", 600, 1);
            Check.That(overview.AffectedBy(@"C:\Docs\nested", Changes(Entry(@"C:\Docs\nested\a.txt"))), "folder overview missed nested changes");
        });

        yield return ("busy_folder_totals_finish_before_one_coalesced_refresh", "A burst of folder changes cannot continually cancel a running count; it finishes and requests one follow-up using the existing totals view.", () =>
        {
            var state = new PreviewRefreshState();
            var first = Request(Entry(@"C:\Docs\selected", EntryKind.Folder));
            long generation = state.Begin(first)!.Value;
            var latest = Request(Entry(@"C:\Docs\selected", EntryKind.Folder, revision: 1));
            for (int i = 0; i < 100; i++)
            {
                Check.Equal(PreviewRefreshAction.AfterCurrent, state.Invalidate(@"C:\Docs\selected", Changes(Entry(@"C:\Docs\selected\log.txt", revision: i))));
                Check.That(state.Begin(latest) is null, "busy folder count was restarted before finishing");
            }
            Check.That(state.Complete(generation), "changed totals did not request a follow-up");
            Check.That(first.CanReuseTotals(latest), "totals update would clear the existing view");
            long next = state.Begin(latest)!.Value;
            Check.That(!state.Complete(generation), "stale completion scheduled another refresh");
            Check.Equal(PreviewRefreshAction.AfterCurrent, state.Invalidate(@"C:\Docs\selected", Changes(Entry(@"C:\Docs\selected\log.txt"))));
            Check.That(state.Complete(next), "new count was incorrectly completed by the old generation");
            long final = state.Begin(latest)!.Value;
            Check.That(!state.Complete(final) && state.Begin(latest) is null, "settled totals kept refreshing");
        });

        yield return ("preview_cancellation_retries_unfinished_work_and_preserves_ready_content", "Interrupted reads retry; a completed preview survives a drag ending with the same selection; a pending shell thumbnail still retries.", () =>
        {
            var state = new PreviewRefreshState();
            var request = Request(Entry(@"C:\Docs\report.pdf"));
            long old = state.Begin(request)!.Value;
            state.Cancel();
            long retry = state.Begin(request)!.Value;
            state.Complete(old);
            state.Complete(retry);
            state.Cancel();
            Check.That(state.Begin(request) is null, "completed preview was rebuilt after a harmless drag");
            state.Cancel(pendingThumbnail: true);
            Check.That(state.Begin(request) is not null, "interrupted thumbnail was never retried");
        });

        yield return ("preview_identity_includes_metadata_layout_names_and_selection_set", "DPI, displayed names, cloud attributes, context and real metadata changes refresh the preview; selection ordering alone does not.", () =>
        {
            var file = Entry(@"C:\Docs\report.pdf");
            var original = Request(file);
            Check.That(!original.Matches(Request(Entry(file.FullPath, size: 11))), "size change ignored");
            Check.That(!original.Matches(Request(Entry(file.FullPath, revision: 1))), "write-time change ignored");
            Check.That(!original.Matches(new([new(file, "report")], @"C:\Docs", 600, 1)), "hidden extension ignored");
            Check.That(!original.Matches(new([new(file, file.Name)], @"C:\Docs", 600, 1.5)), "DPI change ignored");
            Check.That(!original.Matches(new([new(file, file.Name)], @"D:\Other", 600, 1)), "context change ignored");
            var cloud = Entry(@"C:\Docs\folder", EntryKind.Folder, attributes: FileAttributes.Directory | CloudFiles.RecallOnOpen);
            Check.That(!Request(Entry(cloud.FullPath, EntryKind.Folder)).CanReuseTotals(Request(cloud)), "cloud-state transition reused stale rows");
            var other = Entry(@"C:\Docs\other.txt");
            Check.That(Request(file, other).Matches(Request(other, file)), "selection order caused a rebuild");
            var state = new PreviewRefreshState();
            state.Complete(state.Begin(original)!.Value);
            state.Invalidate();
            Check.That(state.Begin(original) is not null, "explicit reload was ignored");
        });

        yield return ("drive_previews_ignore_unrelated_directory_activity", "Drive capacity views do not behave as recursive folder previews; refreshing actual drive metadata still updates them.", () =>
        {
            var changes = Changes(Entry(@"C:\Docs\log.txt", revision: 2));
            Check.That(!Request(Entry(@"C:\", EntryKind.Drive)).AffectedBy(@"C:\Docs", changes), "drive preview refreshed for a descendant file");
            Check.That(!new PreviewRequest([], @"C:\", 600, 1).AffectedBy(@"C:\Docs", changes), "root capacity overview refreshed for a descendant file");
        });
    }
}
