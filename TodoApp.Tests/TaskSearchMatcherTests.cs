using System;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

public class TaskSearchMatcherTests
{
    [Fact]
    public void Matches_EmptyQuery_MatchesEverything()
    {
        var task = new TaskItem { Text = "Buy milk" };

        Assert.True(TaskSearchMatcher.Matches(task, ""));
        Assert.True(TaskSearchMatcher.Matches(task, "   "));
    }

    [Fact]
    public void Matches_PlainText_MatchesTitle()
    {
        var task = new TaskItem { Text = "Buy milk" };

        Assert.True(TaskSearchMatcher.Matches(task, "milk"));
        Assert.False(TaskSearchMatcher.Matches(task, "eggs"));
    }

    [Fact]
    public void Matches_PlainText_MatchesTagsAndBody()
    {
        var task = new TaskItem { Text = "Groceries" };
        task.Tags.Add("errands");
        task.Body.Add(new NoteBlock { Type = NoteBlockType.Text, Text = "Don't forget the coupon" });

        Assert.True(TaskSearchMatcher.Matches(task, "errands"));
        Assert.True(TaskSearchMatcher.Matches(task, "coupon"));
    }

    [Fact]
    public void Matches_TagOperator_FiltersByTag()
    {
        var task = new TaskItem { Text = "Task" };
        task.Tags.Add("work");

        Assert.True(TaskSearchMatcher.Matches(task, "tag:work"));
        Assert.False(TaskSearchMatcher.Matches(task, "tag:home"));
    }

    [Fact]
    public void Matches_IsOverdue_MatchesOnlyOverdueOpenTasks()
    {
        var overdue = new TaskItem { Text = "Task", DueDate = DateTime.Today.AddDays(-1) };
        var doneOverdue = new TaskItem { Text = "Task", DueDate = DateTime.Today.AddDays(-1), IsDone = true };
        var future = new TaskItem { Text = "Task", DueDate = DateTime.Today.AddDays(1) };

        Assert.True(TaskSearchMatcher.Matches(overdue, "is:overdue"));
        Assert.False(TaskSearchMatcher.Matches(doneOverdue, "is:overdue"));
        Assert.False(TaskSearchMatcher.Matches(future, "is:overdue"));
    }

    [Fact]
    public void Matches_IsPinned_MatchesOnlyPinnedTasks()
    {
        var pinned = new TaskItem { Text = "Task", IsPinned = true };
        var unpinned = new TaskItem { Text = "Task" };

        Assert.True(TaskSearchMatcher.Matches(pinned, "is:pinned"));
        Assert.False(TaskSearchMatcher.Matches(unpinned, "is:pinned"));
    }

    [Fact]
    public void Matches_IsRecurring_MatchesOnlyRecurringTasks()
    {
        var recurring = new TaskItem { Text = "Task", Recurrence = RecurrenceRule.Daily };
        var oneOff = new TaskItem { Text = "Task", Recurrence = RecurrenceRule.None };

        Assert.True(TaskSearchMatcher.Matches(recurring, "is:recurring"));
        Assert.False(TaskSearchMatcher.Matches(oneOff, "is:recurring"));
    }

    [Fact]
    public void Matches_IsDone_MatchesOnlyCompletedTasks()
    {
        var done = new TaskItem { Text = "Task", IsDone = true };
        var open = new TaskItem { Text = "Task" };

        Assert.True(TaskSearchMatcher.Matches(done, "is:done"));
        Assert.False(TaskSearchMatcher.Matches(open, "is:done"));
    }

    [Fact]
    public void Matches_HasLink_MatchesOnlyTasksWithALinkBlock()
    {
        var withLink = new TaskItem { Text = "Task" };
        withLink.Body.Add(new NoteBlock { Type = NoteBlockType.Link, Url = "https://example.com" });
        var withoutLink = new TaskItem { Text = "Task" };

        Assert.True(TaskSearchMatcher.Matches(withLink, "has:link"));
        Assert.False(TaskSearchMatcher.Matches(withoutLink, "has:link"));
    }

    [Fact]
    public void Matches_DueToday_MatchesOnlyTasksDueToday()
    {
        var dueToday = new TaskItem { Text = "Task", DueDate = DateTime.Today };
        var dueTomorrow = new TaskItem { Text = "Task", DueDate = DateTime.Today.AddDays(1) };

        Assert.True(TaskSearchMatcher.Matches(dueToday, "due:today"));
        Assert.False(TaskSearchMatcher.Matches(dueTomorrow, "due:today"));
    }

    [Fact]
    public void Matches_OperatorCombinedWithFreeText_RequiresBoth()
    {
        var overdueGroceries = new TaskItem { Text = "Buy groceries", DueDate = DateTime.Today.AddDays(-1) };
        var overdueOther = new TaskItem { Text = "Call dentist", DueDate = DateTime.Today.AddDays(-1) };

        Assert.True(TaskSearchMatcher.Matches(overdueGroceries, "is:overdue groceries"));
        Assert.False(TaskSearchMatcher.Matches(overdueOther, "is:overdue groceries"));
    }

    [Fact]
    public void Matches_UnrecognizedOperatorValue_TreatedAsNoOpNotExclusion()
    {
        var task = new TaskItem { Text = "Task" };

        // A typo like "is:overdu" shouldn't silently hide every task - same behavior as
        // Tasky Web's applySearch.
        Assert.True(TaskSearchMatcher.Matches(task, "is:overdu"));
    }

    // ROADMAP.md #122: checklist items, attachment filenames, and link metadata weren't searchable.
    [Fact]
    public void Matches_PlainText_MatchesChecklistItemText()
    {
        var task = new TaskItem { Text = "Groceries" };
        var checklist = new NoteBlock { Type = NoteBlockType.Checklist };
        checklist.ChecklistItems.Add(new ChecklistItem { Text = "Sourdough bread" });
        task.Body.Add(checklist);

        Assert.True(TaskSearchMatcher.Matches(task, "sourdough"));
        Assert.False(TaskSearchMatcher.Matches(task, "croissant"));
    }

    [Fact]
    public void Matches_PlainText_MatchesAttachmentFileName()
    {
        var task = new TaskItem { Text = "Task" };
        task.Body.Add(new NoteBlock { Type = NoteBlockType.File, PhotoPath = @"C:\attachments\invoice.pdf" });

        Assert.True(TaskSearchMatcher.Matches(task, "invoice"));
    }

    [Fact]
    public void Matches_PlainText_MatchesLinkLabelAndUrl()
    {
        var task = new TaskItem { Text = "Task" };
        task.Body.Add(new NoteBlock { Type = NoteBlockType.Link, Url = "https://example.com/receipt", LinkLabel = "Order receipt" });

        Assert.True(TaskSearchMatcher.Matches(task, "receipt"));
        Assert.True(TaskSearchMatcher.Matches(task, "example.com"));
    }

    [Fact]
    public void Matches_DueWeek_MatchesTasksDueWithinSevenDays()
    {
        var dueTomorrow = new TaskItem { Text = "Task", DueDate = DateTime.Today.AddDays(1) };
        var dueInEightDays = new TaskItem { Text = "Task", DueDate = DateTime.Today.AddDays(8) };
        var overdue = new TaskItem { Text = "Task", DueDate = DateTime.Today.AddDays(-1) };
        var noDueDate = new TaskItem { Text = "Task" };

        Assert.True(TaskSearchMatcher.Matches(dueTomorrow, "due:week"));
        Assert.False(TaskSearchMatcher.Matches(dueInEightDays, "due:week"));
        Assert.False(TaskSearchMatcher.Matches(overdue, "due:week"));
        Assert.False(TaskSearchMatcher.Matches(noDueDate, "due:week"));
    }

    [Fact]
    public void Matches_DueNone_MatchesOnlyTasksWithoutADueDate()
    {
        var noDueDate = new TaskItem { Text = "Task" };
        var withDueDate = new TaskItem { Text = "Task", DueDate = DateTime.Today };

        Assert.True(TaskSearchMatcher.Matches(noDueDate, "due:none"));
        Assert.False(TaskSearchMatcher.Matches(withDueDate, "due:none"));
    }
}
