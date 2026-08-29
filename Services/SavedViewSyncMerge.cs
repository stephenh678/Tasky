using System;
using System.Collections.Generic;
using System.Linq;
using TodoApp.Models;

namespace TodoApp.Services;

// A much simpler counterpart to TaskSyncMerge: saved views aren't collaboratively edited the way
// tasks are (no field-level conflicts to reconcile, no "which edit is newer" question worth
// answering), so a plain additive union by Id - minus whatever either side has tombstoned - is
// enough. Kept as a small pure static function for the same reason TaskSyncMerge.ComputeMergePlan
// is: unit-testable without constructing a MainViewModel.
public static class SavedViewSyncMerge
{
    public static (List<SavedView> MergedViews, List<string> MergedDeletedIds) Merge(
        IEnumerable<SavedView> local,
        IEnumerable<SavedView> remote,
        IEnumerable<string> localDeletedIds,
        IEnumerable<string> remoteDeletedIds)
    {
        var deletedIds = new HashSet<string>(localDeletedIds, StringComparer.Ordinal);
        deletedIds.UnionWith(remoteDeletedIds);

        // remote first, then local - local overwrites on a same-Id collision, an arbitrary but
        // deterministic tiebreak for the rare case a view was somehow edited differently on two
        // devices before either synced.
        var merged = new Dictionary<string, SavedView>(StringComparer.Ordinal);
        foreach (var view in remote.Concat(local))
        {
            if (!deletedIds.Contains(view.Id))
                merged[view.Id] = view;
        }

        return (merged.Values.ToList(), deletedIds.ToList());
    }
}
