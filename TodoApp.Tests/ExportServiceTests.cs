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
