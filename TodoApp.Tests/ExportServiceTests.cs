using System;
using System.Collections.Generic;
using System.IO;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

public class ExportToICalendarTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;

    public ExportToICalendarTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TaskyTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "export.ics");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void NoTasksWithDueDates_WritesAnEmptyCalendarAndReturnsZero()
    {
        var count = ExportService.ExportToICalendar(new List<TaskItem> { new() { Text = "No due date" } }, _filePath);

        Assert.Equal(0, count);
        var content = File.ReadAllText(_filePath);
        Assert.Contains("BEGIN:VCALENDAR", content);
        Assert.Contains("END:VCALENDAR", content);
        Assert.DoesNotContain("BEGIN:VEVENT", content);
    }

    [Fact]
    public void ClosedTask_IsExcludedEvenWithADueDate()
    {
        var task = new TaskItem { Text = "Trashed", DueDate = new DateTime(2026, 3, 1), IsClosed = true };
        var count = ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        Assert.Equal(0, count);
    }

    [Fact]
    public void MidnightDueDate_IsExportedAsAnAllDayEvent()
    {
        var task = new TaskItem { Text = "All day task", DueDate = new DateTime(2026, 3, 15) };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        var content = File.ReadAllText(_filePath);
        Assert.Contains("DTSTART;VALUE=DATE:20260315", content);
        Assert.Contains("DTEND;VALUE=DATE:20260316", content); // exclusive end, one day later
    }

    [Fact]
    public void DueDateWithTime_IsExportedAsA30MinuteTimedEvent()
    {
        var task = new TaskItem { Text = "Timed task", DueDate = new DateTime(2026, 3, 15, 15, 0, 0) };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        var content = File.ReadAllText(_filePath);
        Assert.Contains("DTSTART:20260315T150000", content);
        Assert.Contains("DTEND:20260315T153000", content);
    }

    [Fact]
    public void CompletedTask_SummaryIsPrefixedWithDone()
    {
        var task = new TaskItem { Text = "Finished thing", DueDate = new DateTime(2026, 3, 15), IsDone = true };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        Assert.Contains("SUMMARY:[Done] Finished thing", File.ReadAllText(_filePath));
    }

    [Fact]
    public void TagsArePresent_DescriptionListsThem()
    {
        var task = new TaskItem { Text = "Tagged", DueDate = new DateTime(2026, 3, 15) };
        task.Tags.Add("finance");
        task.Tags.Add("urgent");
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        Assert.Contains("DESCRIPTION:Tags: finance\\, urgent", File.ReadAllText(_filePath));
    }

    [Fact]
    public void NoTags_NoDescriptionLineIsWritten()
    {
        var task = new TaskItem { Text = "Untagged", DueDate = new DateTime(2026, 3, 15) };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        Assert.DoesNotContain("DESCRIPTION:", File.ReadAllText(_filePath));
    }

    [Fact]
    public void SpecialCharactersInTitle_AreEscapedPerRfc5545()
    {
        var task = new TaskItem { Text = "Buy milk, eggs; bread \\ butter", DueDate = new DateTime(2026, 3, 15) };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        Assert.Contains(@"SUMMARY:Buy milk\, eggs\; bread \\ butter", File.ReadAllText(_filePath));
    }

    [Fact]
    public void EachTask_GetsAStableUidBasedOnItsId()
    {
        var task = new TaskItem { Text = "Task", DueDate = new DateTime(2026, 3, 15) };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        Assert.Contains($"UID:{task.Id}@tasky.app", File.ReadAllText(_filePath));
    }

    [Fact]
    public void MultipleTasks_EachProducesItsOwnEvent()
    {
        var tasks = new List<TaskItem>
        {
            new() { Text = "First", DueDate = new DateTime(2026, 3, 1) },
            new() { Text = "Second", DueDate = new DateTime(2026, 3, 2) },
            new() { Text = "No due date" },
        };

        var count = ExportService.ExportToICalendar(tasks, _filePath);

        Assert.Equal(2, count);
        var content = File.ReadAllText(_filePath);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(content, "BEGIN:VEVENT").Count);
    }

    [Fact]
    public void LongTitle_IsFoldedAcrossContinuationLinesPerRfc5545()
    {
        var longText = new string('a', 200);
        var task = new TaskItem { Text = longText, DueDate = new DateTime(2026, 3, 15) };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        var lines = File.ReadAllLines(_filePath);
        Assert.True(lines.Length > 0);
        foreach (var line in lines)
            Assert.True(line.Length <= 75, $"Line exceeded 75 chars: \"{line}\"");

        // Reassembling folded lines (continuation lines start with a single space) should recover
        // the original unfolded content.
        var content = File.ReadAllText(_filePath);
        var unfolded = content.Replace("\r\n ", "");
        Assert.Contains(longText, unfolded);
    }

    [Fact]
    public void Output_HasWellFormedVCalendarStructure()
    {
        var task = new TaskItem { Text = "Task", DueDate = new DateTime(2026, 3, 15) };
        ExportService.ExportToICalendar(new List<TaskItem> { task }, _filePath);

        var content = File.ReadAllText(_filePath);
        Assert.StartsWith("BEGIN:VCALENDAR", content);
        Assert.Contains("VERSION:2.0", content);
        Assert.Contains("PRODID:", content);
        Assert.EndsWith("END:VCALENDAR", content.TrimEnd());
    }
}

// ROADMAP.md #135: whole-list export. Reads Body's plain-text mirror directly (see
// ExportService.AppendBodyAsMarkdown/AppendBodyAsHtml's own doc comment) rather than a
// FlowDocument, unlike the per-note ExportToMarkdown/ExportToHtml above - so these tests build
// TaskItem.Body directly instead of a FlowDocument fixture.
public class ExportAllTests : IDisposable
{
    private readonly string _dir;

    public ExportAllTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TaskyTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static TaskItem MakeTask(string text, bool isDone = false, bool isClosed = false)
    {
        var task = new TaskItem { Text = text, IsDone = isDone, IsClosed = isClosed };
        task.Body.Clear();
        return task;
    }

    [Fact]
    public void Markdown_SkipsTrashedTasks()
    {
        var path = Path.Combine(_dir, "export.md");
        var open = MakeTask("Keep me");
        var trashed = MakeTask("Drop me", isClosed: true);

        ExportService.ExportAllToMarkdown(new List<TaskItem> { open, trashed }, path);

        var content = File.ReadAllText(path);
        Assert.Contains("Keep me", content);
        Assert.DoesNotContain("Drop me", content);
    }

    [Fact]
    public void Markdown_IncludesDueDateTagsAndChecklistState()
    {
        var path = Path.Combine(_dir, "export.md");
        var task = MakeTask("Pack for trip");
        task.DueDate = new DateTime(2026, 6, 1);
        task.Tags.Add("travel");
        task.Body.Add(new NoteBlock
        {
            Type = NoteBlockType.Checklist,
            ChecklistItems =
            {
                new ChecklistItem { Text = "Passport", IsChecked = true },
                new ChecklistItem { Text = "Sunscreen", IsChecked = false },
            }
        });

        ExportService.ExportAllToMarkdown(new List<TaskItem> { task }, path);

        var content = File.ReadAllText(path);
        Assert.Contains("2026-06-01", content);
        Assert.Contains("`travel`", content);
        Assert.Contains("- [x] Passport", content);
        Assert.Contains("- [ ] Sunscreen", content);
    }

    [Fact]
    public void Markdown_MarksCompletedTasksInTheHeading()
    {
        var path = Path.Combine(_dir, "export.md");
        var task = MakeTask("Done thing", isDone: true);

        ExportService.ExportAllToMarkdown(new List<TaskItem> { task }, path);

        Assert.Contains("## [x] Done thing", File.ReadAllText(path));
    }

    [Fact]
    public void Html_SkipsTrashedTasksAndEscapesText()
    {
        var path = Path.Combine(_dir, "export.html");
        var open = MakeTask("<script>alert(1)</script>");
        var trashed = MakeTask("Drop me", isClosed: true);

        ExportService.ExportAllToHtml(new List<TaskItem> { open, trashed }, path);

        var content = File.ReadAllText(path);
        Assert.DoesNotContain("<script>alert(1)</script>", content);
        Assert.Contains("&lt;script&gt;", content);
        Assert.DoesNotContain("Drop me", content);
    }

    [Fact]
    public void Html_IsWellFormedAndIncludesChecklistItems()
    {
        var path = Path.Combine(_dir, "export.html");
        var task = MakeTask("Groceries");
        task.Body.Add(new NoteBlock
        {
            Type = NoteBlockType.Checklist,
            ChecklistItems = { new ChecklistItem { Text = "Milk", IsChecked = true } }
        });

        ExportService.ExportAllToHtml(new List<TaskItem> { task }, path);

        var content = File.ReadAllText(path);
        Assert.StartsWith("<!DOCTYPE html>", content);
        Assert.Contains("Groceries", content);
        Assert.Contains("checked disabled", content);
        Assert.Contains("Milk", content);
    }
}
