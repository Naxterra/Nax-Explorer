using System.Globalization;

namespace NaxExplorer.Core;

public enum SortColumn
{
    Name,
    Modified,
    Type,
    Size,
}

/// <summary>
/// Compares names the way people read them: "file2" comes before "file10", and case does not matter. Runs of digits are
/// compared by their value, everything else with the current culture.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    private static readonly CompareInfo Culture = CultureInfo.CurrentCulture.CompareInfo;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        ReadOnlySpan<char> a = x, b = y;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                int byValue = CompareDigits(a[startA..i], b[startB..j]);
                if (byValue != 0) return byValue;
                continue;
            }
            int textStartA = i, textStartB = j;
            while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
            while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
            int byText = Culture.Compare(a[textStartA..i], b[textStartB..j], CompareOptions.IgnoreCase);
            if (byText != 0) return byText;
        }
        int byLength = (a.Length - i).CompareTo(b.Length - j);
        if (byLength != 0) return byLength;
        // Names that differ only in case or leading zeros still get a stable order.
        return string.CompareOrdinal(x, y);
    }

    private static int CompareDigits(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        ReadOnlySpan<char> trimmedA = a.TrimStart('0'), trimmedB = b.TrimStart('0');
        if (trimmedA.Length != trimmedB.Length) return trimmedA.Length.CompareTo(trimmedB.Length);
        int digits = trimmedA.SequenceCompareTo(trimmedB);
        if (digits != 0) return digits;
        // "01" after "1", so equal values never compare as equal names.
        return a.Length.CompareTo(b.Length);
    }
}

/// <summary>Sorts file lists. Folders and drives always come before files, whichever direction is chosen, as in Explorer.</summary>
public static class EntrySorter
{
    public static Comparison<FileEntry> Comparison(SortColumn column, bool descending)
    {
        return (x, y) =>
        {
            // This PC has no sort headers; keep letter order even after a folder used another sort.
            if (x.Kind == EntryKind.Drive && y.Kind == EntryKind.Drive)
                return StringComparer.OrdinalIgnoreCase.Compare(x.FullPath, y.FullPath);
            int group = (x.IsContainer ? 0 : 1).CompareTo(y.IsContainer ? 0 : 1);
            if (group != 0) return group;
            int result = column switch
            {
                SortColumn.Modified => x.ModifiedUtc.CompareTo(y.ModifiedUtc),
                SortColumn.Size => x.Size.CompareTo(y.Size),
                SortColumn.Type => NaturalComparer.Instance.Compare(x.Extension, y.Extension),
                _ => 0,
            };
            if (result == 0) result = NaturalComparer.Instance.Compare(x.DisplayName, y.DisplayName);
            if (result == 0) result = string.CompareOrdinal(x.FullPath, y.FullPath);
            return descending ? -result : result;
        };
    }

    public static void Sort(List<FileEntry> entries, SortColumn column, bool descending) => entries.Sort(Comparison(column, descending));

    /// <summary>
    /// Items in the order of a column, or in their own order when no column is chosen, as search results show the
    /// order the search found them in (by relevance for the smart search) while no column is chosen.
    /// </summary>
    public static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, FileEntry> entry, SortColumn? column, bool descending) =>
        column is { } chosen ? items.OrderBy(entry, Comparer<FileEntry>.Create(Comparison(chosen, descending))) : items;}
