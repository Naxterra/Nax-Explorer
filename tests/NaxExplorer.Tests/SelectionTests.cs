using NaxExplorer.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NaxExplorer.Tests;

internal static class SelectionTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("rename_repositions_one_row_without_resetting_large_list", "A rename that changes sort position preserves all 10,000 row instances and sends one Move notification instead of clearing or replacing the file list.", () =>
        {
            var rows = new ObservableCollection<RenameRow>(Enumerable.Range(0, 10_000).Select(i => new RenameRow(i.ToString("D5"))));
            var before = rows.ToArray();
            var notifications = new List<NotifyCollectionChangedAction>();
            rows.CollectionChanged += (_, change) => notifications.Add(change.Action);
            var renamed = rows[5];
            renamed.Name = "99999";
            int comparisons = 0;
            ListingOrder.Reposition(rows, renamed, (a, b) => { comparisons++; return StringComparer.Ordinal.Compare(a.Name, b.Name); });
            Check.That(ReferenceEquals(rows[^1], renamed), "renamed row did not move to its new sorted position");
            Check.Sequence(before.Where(row => !ReferenceEquals(row, renamed)), rows.Take(rows.Count - 1));
            Check.Sequence(new[] { NotifyCollectionChangedAction.Move }, notifications);
            Check.That(comparisons < 20, "reordering scanned the full listing");
            notifications.Clear();
            ListingOrder.Reposition(rows, renamed, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            Check.Equal(0, notifications.Count);
            renamed.Name = "-first";
            ListingOrder.Reposition(rows, renamed, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            Check.That(ReferenceEquals(rows[0], renamed), "rename towards the start failed");
            Check.Sequence(new[] { NotifyCollectionChangedAction.Move }, notifications);
        });

        yield return ("rename_order_handles_reverse_sort_single_rows_and_missing_items", "Renamed rows respect descending order, and an empty list, single row or vanished row cannot reset the listing.", () =>
        {
            var first = new RenameRow("z");
            var middle = new RenameRow("m");
            var last = new RenameRow("a");
            var rows = new ObservableCollection<RenameRow>([first, middle, last]);
            middle.Name = "zz";
            ListingOrder.Reposition(rows, middle, (a, b) => StringComparer.Ordinal.Compare(b.Name, a.Name));
            Check.Sequence(new[] { middle, first, last }, rows);
            ListingOrder.Reposition(rows, new RenameRow("missing"), (a, b) => 0);
            var single = new ObservableCollection<RenameRow>([first]);
            single.CollectionChanged += (_, _) => throw new Exception("single row changed");
            ListingOrder.Reposition(single, first, (a, b) => 0);
            ListingOrder.Reposition(new List<RenameRow>(), first, (a, b) => 0);
        });

        yield return ("drop_modifiers_override_drive_defaults_for_every_source", "Ctrl copies and Shift moves on the same or another drive; mixed-drive unmodified drops copy, and UNC roots compare without case sensitivity.", () =>
        {
            Check.That(DropPolicy.Moves(false, false, [@"C:\source\a"], @"C:\target"), "same-drive default should move");
            Check.That(!DropPolicy.Moves(false, false, [@"C:\a", @"D:\b"], @"C:\target"), "mixed-drive sources should copy");
            foreach (string target in new[] { @"C:\target", @"D:\target" })
            {
                Check.That(!DropPolicy.Moves(true, false, [@"C:\a"], target), "Ctrl did not force copy");
                Check.That(DropPolicy.Moves(false, true, [@"C:\a"], target), "Shift did not force move");
            }
            Check.That(!DropPolicy.Moves(false, false, null, @"C:\target"), "unknown source should copy");
            Check.That(DropPolicy.Moves(false, true, null, @"D:\target"), "external Shift drag lost its move choice");
            Check.That(DropPolicy.Moves(false, false, [@"\\server\share\a"], @"\\SERVER\SHARE\b"), "same UNC share should move");
            Check.That(!DropPolicy.Moves(false, false, [@"\\server\share\a"], @"\\server\other\b"), "different shares should copy");
        });
        yield return ("marquee_input_coalesces_and_release_flushes_latest", "A burst of mouse messages produces one update at the newest point; cancellation discards pending work.", () =>
        {
            var input = new FrameInput<int>();
            for (int i = 0; i < 1000; i++) input.Submit(i);
            Check.That(input.TryTake(out int value), "latest input was lost");
            Check.Equal(999, value);
            Check.That(!input.TryTake(out _), "input was applied twice");
            input.Submit(1001);
            Check.That(input.TryTake(out value), "release endpoint was lost");
            Check.Equal(1001, value);
            input.Submit(7);
            input.Clear();
            Check.That(!input.TryTake(out _), "cancelled input leaked into the next gesture");
        });

        yield return ("marquee_virtual_rows_survive_scroll_and_reverse", "A box includes unrealized rows in content coordinates and can shrink after reversing direction.", () =>
        {
            var grid = new MarqueeGrid(4, 2, 1000, 30, 1000, 30, 1);
            Check.Sequence(new[] { (0, 100_000u) }, grid.Intersect(10, 2, 800, 3_000_002, 100_000));
            Check.Sequence(new[] { (10, 20u) }, grid.Intersect(10, 302, 800, 902, 100_000));
            var selection = new RangeSelection([]);
            var expanded = selection.Update(grid.Intersect(10, 2, 800, 3002, 100_000));
            Check.Sequence(new[] { (0, 100u) }, expanded.Added);
            var shrunk = selection.Update(grid.Intersect(10, 302, 800, 902, 100_000));
            Check.Sequence(new[] { (0, 10u), (30, 70u) }, shrunk.Removed);
            Check.Equal(0, shrunk.Added.Length);
        });

        yield return ("marquee_thumbnail_gaps_partial_row_and_exact_edges", "Grid hit testing excludes gaps and touching edges, respects columns, and clips the final partial row.", () =>
        {
            var grid = new MarqueeGrid(8, 4, 150, 172, 160, 180, 3);
            Check.Equal(0, grid.Intersect(158, 4, 168, 176, 8).Count());
            Check.Sequence(new[] { (1, 1u), (4, 1u), (7, 1u) }, grid.Intersect(170, 4, 300, 600, 8));
            Check.Sequence(new[] { (0, 8u) }, grid.Intersect(0, 0, 500, 600, 8));
            Check.Equal(0, grid.Intersect(0, 0, 500, 600, 0).Count());
        });

        yield return ("marquee_ranges_retain_ctrl_selection_and_bound_work", "Selecting 100,000 rows uses one range, overlapping retained ranges survive shrinking, and repeated frames make no writes.", () =>
        {
            var selection = new RangeSelection([(10, 10), (15, 10), (100_000, 1)]);
            var first = selection.Update([(0, 100_000)]);
            Check.Sequence(new[] { (0, 10u), (25, 99_975u) }, first.Added);
            Check.Equal(0, selection.Update([(0, 100_000)]).Added.Length);
            var shrunk = selection.Update([(20, 10)]);
            Check.Sequence(new[] { (0, 10u), (30, 99_970u) }, shrunk.Removed);
            Check.Equal(0, shrunk.Added.Length);
            var empty = selection.Update([]);
            Check.Sequence(new[] { (25, 5u) }, empty.Removed);
            var extreme = new RangeSelection([]).Update([(int.MaxValue, 1)]);
            Check.Sequence(new[] { (int.MaxValue, 1u) }, extreme.Added);
        });

        yield return ("marquee_edge_scroll_stops_in_middle_and_reverses", "Holding the pointer at a viewport edge scrolls at a bounded speed; returning to the middle stops and the opposite edge reverses.", () =>
        {
            Check.Equal(-900d, MarqueeScroll.Speed(-50, 1000));
            Check.Equal(900d, MarqueeScroll.Speed(1100, 1000));
            Check.Equal(0d, MarqueeScroll.Speed(500, 1000));
            Check.That(MarqueeScroll.Speed(24, 1000) < 0 && MarqueeScroll.Speed(976, 1000) > 0, "edge direction wrong");
            Check.Equal(0d, MarqueeScroll.Speed(1, 0));
        });

        yield return ("marquee_range_deltas_match_set_selection", "Random expansion, disjoint ranges and reversals match an independent item-set model with retained selection.", () =>
        {
            var random = new Random(167);
            var retained = new HashSet<int>([4, 5, 6, 90]);
            var selected = new HashSet<int>(retained);
            var selection = new RangeSelection([(4, 3), (90, 1)]);
            for (int frame = 0; frame < 500; frame++)
            {
                var ranges = Enumerable.Range(0, random.Next(8)).Select(_ => (Start: random.Next(100), Length: (uint)random.Next(20))).ToArray();
                var expected = new HashSet<int>(retained);
                foreach (var range in ranges) expected.UnionWith(Enumerable.Range(range.Start, (int)range.Length));
                var delta = selection.Update(ranges);
                foreach (var range in delta.Removed)
                    foreach (int index in Enumerable.Range(range.Start, (int)range.Length)) Check.That(selected.Remove(index), "removed an unselected row");
                foreach (var range in delta.Added)
                    foreach (int index in Enumerable.Range(range.Start, (int)range.Length)) Check.That(selected.Add(index), "added an already selected row");
                Check.That(selected.SetEquals(expected), $"selection mismatch at frame {frame}");
            }
        });

        yield return ("marquee_changed_indices_form_minimal_native_runs", "Unordered duplicate changes produce disjoint contiguous ranges without integer overflow.", () =>
        {
            Check.Sequence(new[] { (1, 3u), (9, 1u), (int.MaxValue - 1, 2u) }, SelectionRuns.FromIndices([3, 1, 2, 2, -1, 9, int.MaxValue, int.MaxValue - 1]));
            Check.Equal(0, SelectionRuns.FromIndices([]).Count());
        });

        yield return ("marquee_changes_only_rows_entering_or_leaving_the_box", "Expanding, shrinking and reversing a selection box adds/removes only changed rows; an unchanged pointer position performs no selection writes.", () =>
        {
            var gesture = new MarqueeSelection<int>([]);
            var selected = new HashSet<int>();
            void Move(int[] inside)
            {
                var (added, removed) = gesture.Update(inside);
                Check.That(added.All(item => !selected.Contains(item)), "already selected row was added again");
                Check.That(removed.All(selected.Contains), "unselected row was removed again");
                selected.ExceptWith(removed);
                selected.UnionWith(added);
                Check.That(selected.SetEquals(inside), "selection did not match the box");
            }
            Move([1, 2]);
            var unchanged = gesture.Update([2, 1, 1]);
            Check.Equal(0, unchanged.Added.Count + unchanged.Removed.Count);
            Move([1, 2, 3, 4]);
            Move([2]);
            Move([4, 5]);
            Move([]);
        });

        yield return ("marquee_keeps_ctrl_selection_after_the_box_moves_away", "Existing selection is retained when Ctrl or Shift starts the box, including rows the box later crosses and leaves.", () =>
        {
            var gesture = new MarqueeSelection<int>([1, 1000]);
            var initial = gesture.Update([1, 2, 3]);
            Check.Sequence(new[] { 2, 3 }, initial.Added.Order());
            Check.Equal(0, initial.Removed.Count);
            var next = gesture.Update([4, 1000]);
            Check.Sequence(new[] { 4 }, next.Added);
            Check.Sequence(new[] { 2, 3 }, next.Removed.Order());
            var end = gesture.Update([]);
            Check.Sequence(new[] { 4 }, end.Removed);
        });

        yield return ("marquee_mouse_moves_do_not_rescan_a_large_retained_selection", "With 100,000 retained rows, 100 mouse moves over at most 40 visible rows use bounded set lookups instead of rescanning the retained selection each frame.", () =>
        {
            var retained = Enumerable.Range(0, 100_000).Select(i => new CountedItem(i)).ToArray();
            var gesture = new MarqueeSelection<CountedItem>(retained);
            var visible = Enumerable.Range(99_990, 40).Select(i => new CountedItem(i)).ToArray();
            CountedItem.HashCalls = 0;
            for (int i = 0; i < 100; i++) gesture.Update(visible.Take(i % 40));
            Check.That(CountedItem.HashCalls < 30_000, $"mouse movement scanned the large selection ({CountedItem.HashCalls} lookups)");
        });
    }

    private sealed class RenameRow(string name) { public string Name { get; set; } = name; }

    private sealed record CountedItem(int Id)
    {
        public static int HashCalls;
        public override int GetHashCode() { HashCalls++; return Id; }
    }
}
