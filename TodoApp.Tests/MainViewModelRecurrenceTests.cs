using System;
using TodoApp.Models;
using TodoApp.ViewModels;

namespace TodoApp.Tests;

// ROADMAP.md #31 follow-up: NextDueDate/RecurrenceAnchor used to advance straight from a task's
// (possibly stale) DueDate, so completing a long-overdue recurring task spawned a next occurrence
// that was still overdue. RecurrenceAnchor clamps the anchor to today when the task was already
// overdue, before NextDueDate applies the rule/interval on top - covers just the new clamping
// logic, not the pre-existing interval math (already exercised via docs/js/test/parity.test.js's
// nextDueDate/spawnNextOccurrence suite, which this mirrors).
public class MainViewModelRecurrenceTests
{
    [Fact]
    public void RecurrenceAnchor_OverdueDueDate_ClampsToTodayButKeepsTimeOfDay()
    {
        var overdue = DateTime.Today.AddDays(-14).AddHours(17); // 14 days overdue, due at 5pm
        var anchor = MainViewModel.RecurrenceAnchor(overdue);
        Assert.Equal(DateTime.Today, anchor.Date);
        Assert.Equal(overdue.TimeOfDay, anchor.TimeOfDay);
    }

    [Fact]
    public void RecurrenceAnchor_DueToday_IsUnchanged()
    {
        var dueToday = DateTime.Today.AddHours(9);
        var anchor = MainViewModel.RecurrenceAnchor(dueToday);
        Assert.Equal(dueToday, anchor);
    }

    [Fact]
    public void RecurrenceAnchor_DueInFuture_IsUnchanged()
    {
        var dueNextWeek = DateTime.Today.AddDays(7).AddHours(12);
        var anchor = MainViewModel.RecurrenceAnchor(dueNextWeek);
        Assert.Equal(dueNextWeek, anchor);
    }

    [Fact]
    public void RecurrenceAnchor_NoDueDate_DefaultsToToday()
    {
        var anchor = MainViewModel.RecurrenceAnchor(null);
        Assert.Equal(DateTime.Today, anchor.Date);
    }

    [Fact]
    public void CompletingLongOverdueDailyTask_SpawnsOccurrenceDueTomorrow_NotStillOverdue()
    {
        var overdue = DateTime.Today.AddDays(-14);
        var next = MainViewModel.NextDueDate(MainViewModel.RecurrenceAnchor(overdue), RecurrenceRule.Daily, interval: 1);
        Assert.Equal(DateTime.Today.AddDays(1), next.Date);
    }

    [Fact]
    public void CompletingOverdueWeeklyTaskWithInterval_AdvancesFromToday_NotFromStaleDueDate()
    {
        var overdue = DateTime.Today.AddDays(-30);
        var next = MainViewModel.NextDueDate(MainViewModel.RecurrenceAnchor(overdue), RecurrenceRule.Weekly, interval: 2);
        Assert.Equal(DateTime.Today.AddDays(14), next.Date);
    }
}
