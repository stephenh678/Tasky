using System;
using System.Collections.Generic;
using System.Linq;
using TodoApp.Models;

namespace TodoApp.Services;

// A single day cell in the calendar month grid - a plain projection rebuilt wholesale by
// CalendarGridBuilder.BuildMonthGrid whenever the visible month or the underlying task list
// changes, rather than a live view model kept in sync field-by-field. Tasks holds direct TaskItem
// references (not copies), so edits to a task's own bindable properties (its title, done state)
// still show up live on an already-rendered pill without a rebuild - only a change to which day a
// task belongs on (DueDate) or whether it should show at all (IsClosed) needs one.
public class CalendarDay
{
    public DateTime Date { get; init; }
    public bool IsCurrentMonth { get; init; }
    public bool IsToday { get; init; }
    public IReadOnlyList<TaskItem> Tasks { get; init; } = Array.Empty<TaskItem>();
}

/// <summary>
/// Pure calendar-grid math, kept separate from MainViewModel so it's unit-testable without
/// constructing a full ViewModel (which loads real settings/theme on construction).
/// </summary>
public static class CalendarGridBuilder
{
    private const int GridCellCount = 42; // 6 weeks x 7 days - enough to always fully cover any month's leading/trailing days

    // month can be any date within the target month (only Year/Month are used); today is passed
    // in rather than read from DateTime.Now/Today so the grid is deterministic and testable.
    public static List<CalendarDay> BuildMonthGrid(DateTime month, IEnumerable<TaskItem> tasks, DateTime today)
    {
        var firstOfMonth = new DateTime(month.Year, month.Month, 1);
        var gridStart = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
        var todayDate = today.Date;

        var tasksByDueDate = tasks
            .Where(t => !t.IsClosed && t.DueDate.HasValue)
            .ToLookup(t => t.DueDate!.Value.Date);

        var days = new List<CalendarDay>(GridCellCount);
        for (var i = 0; i < GridCellCount; i++)
        {
            var date = gridStart.AddDays(i);
            var tasksThatDay = tasksByDueDate[date]
                .OrderByDescending(t => t.IsPinned)
                .ThenBy(t => t.IsDone)
                .ThenBy(t => t.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();

            days.Add(new CalendarDay
            {
                Date = date,
                IsCurrentMonth = date.Month == month.Month,
                IsToday = date == todayDate,
                Tasks = tasksThatDay
            });
        }

        return days;
    }
}
