using System.Collections.ObjectModel;

namespace NaxExplorer.Core;

public static class ListingOrder
{
    /// <summary>Repositions one renamed row without resetting or replacing any other row.</summary>
    public static void Reposition<T>(IList<T> rows, T item, Comparison<T> compare)
    {
        int old = rows.IndexOf(item);
        if (old < 0) return;
        int low = 0, high = rows.Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            int actual = middle >= old ? middle + 1 : middle;
            if (compare(rows[actual], item) <= 0) low = middle + 1;
            else high = middle;
        }
        if (low == old) return;
        if (rows is ObservableCollection<T> observable) observable.Move(old, low);
        else { rows.RemoveAt(old); rows.Insert(low, item); }
    }
}
