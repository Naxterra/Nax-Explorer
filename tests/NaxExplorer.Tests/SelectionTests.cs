using NaxExplorer.Core;

namespace NaxExplorer.Tests;

internal static class SelectionTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
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

    private sealed record CountedItem(int Id)
    {
        public static int HashCalls;
        public override int GetHashCode() { HashCalls++; return Id; }
    }
}
