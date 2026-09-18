using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TodoApp.Models;
using TodoApp.Services;
using TodoApp;

namespace TodoApp.ViewModels;

// Recurring tasks: next-due-date math and spawning the next occurrence. Mirrored by
// docs/js/model.js - keep the two in step.
public partial class MainViewModel
{
    // ROADMAP.md #31: interval multiplies the step (Weekly + interval 2 = every 2 weeks) instead of
    // recurrence being fixed at "every 1". Mirrors docs/js/model.js's nextDueDate exactly.
    //
    // anchorDay (Monthly/Yearly only) is the day-of-month the series belongs on. AddMonths clamps
    // to a short month's last day - correct for that one occurrence, but computing the NEXT one
    // from the clamped date meant a task due on the 31st slid to the 28th in February and never
    // came back. Re-applying the anchor after each step keeps it on the 31st wherever one exists.
    internal static DateTime NextDueDate(DateTime from, RecurrenceRule rule, int interval, int? anchorDay = null) => rule switch
    {
        RecurrenceRule.Daily => from.AddDays(interval),
        RecurrenceRule.Weekly => from.AddDays(7 * interval),
        RecurrenceRule.Monthly => OnAnchorDay(from.AddMonths(interval), anchorDay),
        RecurrenceRule.Yearly => OnAnchorDay(from.AddYears(interval), anchorDay),
        _ => from
    };

    private static DateTime OnAnchorDay(DateTime date, int? anchorDay)
    {
        if (anchorDay is not { } day || day < 1 || day > 31) return date;
        var target = Math.Min(day, DateTime.DaysInMonth(date.Year, date.Month));
        return date.AddDays(target - date.Day);
    }

    // The day-of-month a Monthly/Yearly series should stay on. A stored RecurrenceAnchorDay only
    // counts while the due date is still that day clamped to its month (the 28th of February for a
    // 31st-of-the-month series) - if the user has since picked some other date, that's the new
    // series day. Deliberately read from the task's OWN due date, not RecurrenceAnchor()'s
    // clamped-to-today one: completing "rent, due the 1st" on the 3rd should produce the 1st of
    // next month, not shift the whole series to the 3rd.
    // Mirrors docs/js/model.js's effectiveAnchorDay exactly.
    internal static int? EffectiveAnchorDay(DateTime? dueDate, int? storedAnchorDay)
    {
        if (dueDate is not { } due) return null;
        if (storedAnchorDay is { } stored && stored >= 1 && stored <= 31
            && due.Day == Math.Min(stored, DateTime.DaysInMonth(due.Year, due.Month)))
            return stored;
        return due.Day;
    }

    // Completing a recurring task doesn't just close it out - it spawns the next occurrence
    // (title, due date advanced by the rule/interval, tags) so the series continues. The completed
    // instance still moves into Closed as normal.
    private TaskItem SpawnNextOccurrence(TaskItem completed)
    {
        var isMonthBased = completed.Recurrence is RecurrenceRule.Monthly or RecurrenceRule.Yearly;
        var anchorDay = isMonthBased ? EffectiveAnchorDay(completed.DueDate, completed.RecurrenceAnchorDay) : null;
        var next = new TaskItem
        {
            Text = completed.Text,
            DueDate = NextDueDate(RecurrenceAnchor(completed.DueDate), completed.Recurrence, completed.RecurrenceInterval, anchorDay),
            Recurrence = completed.Recurrence,
            RecurrenceInterval = completed.RecurrenceInterval,
            RecurrenceAnchorDay = anchorDay,
            // Priority is as much a part of "the same task again" as its tags are - the next
            // "!high Pay rent" silently came back with no priority. SortOrder was left at 0, which
            // in Manual sort dropped every new occurrence at the very top, above tasks the user had
            // deliberately arranged; new tasks go last, same as AddTaskCommand.
            Priority = completed.Priority,
            SortOrder = AllTasks.Count > 0 ? AllTasks.Max(t => t.SortOrder) + 1 : 0,
            Tags = new ObservableCollection<string>(completed.Tags)
        };
        AllTasks.Add(next);
        AttachTask(next);
        return next;
    }

    // ROADMAP.md #31: advancing straight from a stale DueDate meant completing a long-overdue
    // recurring task (e.g. a daily task overdue by 2 weeks) spawned a next occurrence that was
    // still overdue, rather than one due tomorrow. Clamp the anchor date to today when the task
    // was already overdue, but keep its time-of-day (e.g. a "@5pm" reminder stays at 5pm) - only
    // the date component was stale, not the time. Mirrors docs/js/model.js's recurrenceAnchor
    // exactly.
    internal static DateTime RecurrenceAnchor(DateTime? dueDate)
    {
        var anchor = dueDate ?? DateTime.Today;
        return anchor.Date < DateTime.Today ? DateTime.Today.Add(anchor.TimeOfDay) : anchor;
    }
}
