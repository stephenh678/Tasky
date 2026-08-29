using System;
using System.Collections.Generic;
using System.Linq;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

public class CalendarGridBuilderTests
{
    private static readonly DateTime Today = new(2026, 3, 15); // a plain Sunday-agnostic anchor; specifics below don't rely on its weekday

    private static TaskItem TaskDue(DateTime dueDate, string text = "task", bool isDone = false, bool isPinned = false, bool isClosed = false) =>
        new() { Text = text, DueDate = dueDate, IsDone = isDone, IsPinned = isPinned, IsClosed = isClosed };

    [Fact]
    public void Grid_AlwaysHasExactly42Cells()
    {
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), Array.Empty<TaskItem>(), Today);
        Assert.Equal(42, grid.Count);
    }

    [Fact]
    public void Grid_FirstCellIsAlwaysASunday_LastCellIsAlwaysASaturday()
    {
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), Array.Empty<TaskItem>(), Today);

        Assert.Equal(DayOfWeek.Sunday, grid[0].Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Saturday, grid[^1].Date.DayOfWeek);
    }

    [Fact]
    public void Grid_DatesAreConsecutiveWithNoGapsOrDuplicates()
    {
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), Array.Empty<TaskItem>(), Today);

        for (var i = 1; i < grid.Count; i++)
            Assert.Equal(grid[i - 1].Date.AddDays(1), grid[i].Date);
    }

    [Fact]
    public void Grid_EveryDayOfTheTargetMonthAppearsExactlyOnceAndIsMarkedCurrentMonth()
    {
        var target = new DateTime(2026, 3, 1);
        var grid = CalendarGridBuilder.BuildMonthGrid(target, Array.Empty<TaskItem>(), Today);
        var daysInMonth = DateTime.DaysInMonth(target.Year, target.Month);

        for (var day = 1; day <= daysInMonth; day++)
        {
            var cell = grid.Single(c => c.Date == new DateTime(target.Year, target.Month, day));
            Assert.True(cell.IsCurrentMonth);
        }
    }

    [Fact]
    public void Grid_LeadingAndTrailingDaysFromAdjacentMonths_AreMarkedNotCurrentMonth()
    {
        var target = new DateTime(2026, 3, 1);
        var grid = CalendarGridBuilder.BuildMonthGrid(target, Array.Empty<TaskItem>(), Today);

        var outsideMonthCells = grid.Where(c => c.Date.Month != target.Month || c.Date.Year != target.Year).ToList();
        Assert.All(outsideMonthCells, c => Assert.False(c.IsCurrentMonth));

        var insideMonthCells = grid.Where(c => c.Date.Month == target.Month && c.Date.Year == target.Year).ToList();
        Assert.All(insideMonthCells, c => Assert.True(c.IsCurrentMonth));
    }

    [Fact]
    public void IsToday_MarksExactlyOneCellWhenTodayFallsWithinTheDisplayedGrid()
    {
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), Array.Empty<TaskItem>(), Today);

        var todayCells = grid.Where(c => c.IsToday).ToList();
        Assert.Single(todayCells);
        Assert.Equal(Today.Date, todayCells[0].Date);
    }

    [Fact]
    public void IsToday_MarksNoCellWhenViewingAFarAwayMonth()
    {
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2020, 1, 1), Array.Empty<TaskItem>(), Today);
        Assert.DoesNotContain(grid, c => c.IsToday);
    }

    [Fact]
    public void TaskWithDueDateInTheMonth_AppearsOnItsExactDay()
    {
        var dueDate = new DateTime(2026, 3, 10);
        var task = TaskDue(dueDate);

        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new[] { task }, Today);

        var cell = grid.Single(c => c.Date == dueDate);
        Assert.Contains(task, cell.Tasks);
        Assert.All(grid.Where(c => c.Date != dueDate), c => Assert.DoesNotContain(task, c.Tasks));
    }

    [Fact]
    public void TaskDueDate_TimeOfDayComponentIsIgnoredForPlacement()
    {
        var task = TaskDue(new DateTime(2026, 3, 10, 15, 30, 0));
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new[] { task }, Today);

        var cell = grid.Single(c => c.Date == new DateTime(2026, 3, 10));
        Assert.Contains(task, cell.Tasks);
    }

    [Fact]
    public void TaskWithNoDueDate_NeverAppearsOnTheGrid()
    {
        var task = new TaskItem { Text = "no due date" };
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new[] { task }, Today);

        Assert.All(grid, c => Assert.DoesNotContain(task, c.Tasks));
    }

    [Fact]
    public void ClosedTask_IsExcludedEvenWithADueDateInRange()
    {
        var task = TaskDue(new DateTime(2026, 3, 10), isClosed: true);
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new[] { task }, Today);

        Assert.All(grid, c => Assert.DoesNotContain(task, c.Tasks));
    }

    [Fact]
    public void DoneTask_IsStillIncluded()
    {
        var task = TaskDue(new DateTime(2026, 3, 10), isDone: true);
        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new[] { task }, Today);

        var cell = grid.Single(c => c.Date == new DateTime(2026, 3, 10));
        Assert.Contains(task, cell.Tasks);
    }

    [Fact]
    public void MultipleTasksOnSameDay_PinnedSortsBeforeUnpinned()
    {
        var pinned = TaskDue(new DateTime(2026, 3, 10), text: "Z pinned", isPinned: true);
        var unpinned = TaskDue(new DateTime(2026, 3, 10), text: "A unpinned");

        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new List<TaskItem> { unpinned, pinned }, Today);
        var cell = grid.Single(c => c.Date == new DateTime(2026, 3, 10));

        Assert.Equal(pinned, cell.Tasks[0]);
        Assert.Equal(unpinned, cell.Tasks[1]);
    }

    [Fact]
    public void MultipleTasksOnSameDay_NotDoneSortsBeforeDone()
    {
        var done = TaskDue(new DateTime(2026, 3, 10), text: "done", isDone: true);
        var notDone = TaskDue(new DateTime(2026, 3, 10), text: "not done");

        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new List<TaskItem> { done, notDone }, Today);
        var cell = grid.Single(c => c.Date == new DateTime(2026, 3, 10));

        Assert.Equal(notDone, cell.Tasks[0]);
        Assert.Equal(done, cell.Tasks[1]);
    }

    [Fact]
    public void MultipleTasksOnSameDay_TiebreaksAlphabeticallyByText()
    {
        var b = TaskDue(new DateTime(2026, 3, 10), text: "Bravo");
        var a = TaskDue(new DateTime(2026, 3, 10), text: "alpha");

        var grid = CalendarGridBuilder.BuildMonthGrid(new DateTime(2026, 3, 1), new List<TaskItem> { b, a }, Today);
        var cell = grid.Single(c => c.Date == new DateTime(2026, 3, 10));

        Assert.Equal(a, cell.Tasks[0]);
        Assert.Equal(b, cell.Tasks[1]);
    }

    [Fact]
    public void Grid_ForMonthWhoseFirstDayIsAlreadyASunday_HasNoLeadingDaysFromThePriorMonth()
    {
        // February 2026: Feb 1 2026 is a Sunday - a real boundary case where the grid should start
        // exactly on the 1st with zero lead-in days from January.
        var target = new DateTime(2026, 2, 1);
        Assert.Equal(DayOfWeek.Sunday, target.DayOfWeek); // sanity-check the premise this test relies on

        var grid = CalendarGridBuilder.BuildMonthGrid(target, Array.Empty<TaskItem>(), Today);

        Assert.Equal(target, grid[0].Date);
        Assert.True(grid[0].IsCurrentMonth);
    }
}
