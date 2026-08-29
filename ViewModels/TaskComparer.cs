using System;
using System.Collections;
using TodoApp.Models;

namespace TodoApp.ViewModels;

public class TaskComparer : IComparer
{
    private readonly SortOption _option;

    public TaskComparer(SortOption option) => _option = option;

    public int Compare(object? x, object? y)
    {
        var a = (TaskItem)x!;
        var b = (TaskItem)y!;

        var pinned = b.IsPinned.CompareTo(a.IsPinned);
        if (pinned != 0) return pinned;

        return _option switch
        {
            SortOption.NameAZ => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase),
            SortOption.NameZA => string.Compare(b.Text, a.Text, StringComparison.OrdinalIgnoreCase),
            SortOption.DueDateSoonest => CompareDueDates(a.DueDate, b.DueDate),
            SortOption.CreatedNewest => b.CreatedAt.CompareTo(a.CreatedAt),
            SortOption.PriorityHighest => b.Priority.CompareTo(a.Priority),
            _ => b.ModifiedAt.CompareTo(a.ModifiedAt)
        };
    }

    private static int CompareDueDates(DateTime? a, DateTime? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        return a.Value.CompareTo(b.Value);
    }
}
