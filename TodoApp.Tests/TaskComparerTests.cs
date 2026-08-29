using System;
using System.Collections.Generic;
using TodoApp.Models;
using TodoApp.ViewModels;

namespace TodoApp.Tests;

public class TaskComparerTests
{
    private static TaskItem Make(string text, bool pinned = false, DateTime? due = null, DateTime? created = null,
        DateTime? modified = null, TaskPriority priority = TaskPriority.None)
    {
        var t = new TaskItem { Text = text, IsPinned = pinned, DueDate = due, Priority = priority };
        if (created.HasValue) t.CreatedAt = created.Value;
        if (modified.HasValue) t.ModifiedAt = modified.Value;
        return t;
    }

    [Fact]
    public void PinnedTasksAlwaysSortBeforeUnpinned_RegardlessOfOption()
    {
        var pinned = Make("Z pinned", pinned: true);
        var unpinned = Make("A unpinned");
        var comparer = new TaskComparer(SortOption.NameAZ);

        Assert.True(comparer.Compare(pinned, unpinned) < 0);
        Assert.True(comparer.Compare(unpinned, pinned) > 0);
    }

    [Fact]
    public void NameAZ_SortsAlphabeticallyIgnoringCase()
    {
        var a = Make("apple");
        var b = Make("Banana");
        var comparer = new TaskComparer(SortOption.NameAZ);

        Assert.True(comparer.Compare(a, b) < 0);
        Assert.True(comparer.Compare(b, a) > 0);
    }

    [Fact]
    public void NameZA_ReversesAlphabeticalOrder()
    {
        var a = Make("apple");
        var b = Make("Banana");
        var comparer = new TaskComparer(SortOption.NameZA);

        Assert.True(comparer.Compare(a, b) > 0);
        Assert.True(comparer.Compare(b, a) < 0);
    }

    [Fact]
    public void DueDateSoonest_OrdersEarlierDatesFirst()
    {
        var soon = Make("soon", due: new DateTime(2026, 1, 1));
        var later = Make("later", due: new DateTime(2026, 6, 1));
        var comparer = new TaskComparer(SortOption.DueDateSoonest);

        Assert.True(comparer.Compare(soon, later) < 0);
    }

    [Fact]
    public void DueDateSoonest_TasksWithoutDueDateSortLast()
    {
        var withDate = Make("has date", due: new DateTime(2026, 1, 1));
        var noDate = Make("no date", due: null);
        var comparer = new TaskComparer(SortOption.DueDateSoonest);

        Assert.True(comparer.Compare(withDate, noDate) < 0);
        Assert.True(comparer.Compare(noDate, withDate) > 0);
        Assert.Equal(0, comparer.Compare(Make("a"), Make("b")));
    }

    [Fact]
    public void CreatedNewest_OrdersMostRecentlyCreatedFirst()
    {
        var older = Make("older", created: new DateTime(2026, 1, 1));
        var newer = Make("newer", created: new DateTime(2026, 6, 1));
        var comparer = new TaskComparer(SortOption.CreatedNewest);

        Assert.True(comparer.Compare(newer, older) < 0);
    }

    [Fact]
    public void PriorityHighest_OrdersHigherPriorityFirst()
    {
        var high = Make("urgent", priority: TaskPriority.High);
        var none = Make("someday", priority: TaskPriority.None);
        var comparer = new TaskComparer(SortOption.PriorityHighest);

        Assert.True(comparer.Compare(high, none) < 0);
        Assert.True(comparer.Compare(none, high) > 0);
    }

    [Fact]
    public void PriorityHighest_TiedPrioritiesCompareEqual()
    {
        var a = Make("a", priority: TaskPriority.Medium);
        var b = Make("b", priority: TaskPriority.Medium);
        var comparer = new TaskComparer(SortOption.PriorityHighest);

        Assert.Equal(0, comparer.Compare(a, b));
    }

    [Fact]
    public void ModifiedNewest_IsTheDefaultForUnknownOption()
    {
        var older = Make("older", modified: new DateTime(2026, 1, 1));
        var newer = Make("newer", modified: new DateTime(2026, 6, 1));
        var comparer = new TaskComparer(SortOption.ModifiedNewest);

        Assert.True(comparer.Compare(newer, older) < 0);
    }

    [Fact]
    public void CanBeUsedDirectlyAsAListSortComparer()
    {
        var list = new List<TaskItem>
        {
            Make("Charlie"),
            Make("Alpha", pinned: true),
            Make("Bravo"),
        };

        var comparer = new TaskComparer(SortOption.NameAZ);
        list.Sort((a, b) => comparer.Compare(a, b));

        Assert.Equal("Alpha", list[0].Text); // pinned wins regardless of name
        Assert.Equal("Bravo", list[1].Text);
        Assert.Equal("Charlie", list[2].Text);
    }
}
