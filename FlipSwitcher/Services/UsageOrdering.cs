using System;
using System.Collections.Generic;
using System.Linq;

namespace FlipSwitcher.Services;

/// <summary>
/// Usage-frequency ordering: items whose process is activated more often come first.
/// WPF-free so the console test project can compile-link it; the sort logic itself
/// lives here (Services layer) rather than in MainViewModel so it is unit-testable.
/// </summary>
public static class UsageOrdering
{
    /// <summary>
    /// Returns the items ordered by usage count descending, with ties preserving the
    /// input order (the window list's Z-/MRU-order). Stability matters: when counts are
    /// equal — including the all-zero cold-start case — the list must look exactly like
    /// it did before this feature existed.
    /// </summary>
    /// <param name="items">Items in their current (Z-)order.</param>
    /// <param name="processKey">Extracts the process name used for count lookup.</param>
    /// <param name="countLookup">Maps a process name to its activation count.</param>
    public static IReadOnlyList<T> Apply<T>(
        IReadOnlyList<T> items,
        Func<T, string> processKey,
        Func<string, int> countLookup)
    {
        // Capture each item's original index BEFORE sorting, then OrderByDescending +
        // ThenBy(originalIndex). This makes the tie-break explicit — List.Sort alone is
        // unstable and could scramble same-count entries.
        var indexed = new List<(T Item, int Index, int Count)>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var key = processKey(items[i]);
            var count = string.IsNullOrEmpty(key) ? 0 : countLookup(key);
            indexed.Add((items[i], i, count));
        }

        return indexed
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();
    }
}
