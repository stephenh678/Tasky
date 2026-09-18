using System;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

public class QuickEntryParserTests
{
    // A fixed Wednesday so weekday-resolution tests are deterministic regardless of when the
    // suite actually runs.
    private static readonly DateTime Reference = new(2026, 3, 4); // Wednesday, March 4 2026

    [Fact]
    public void PlainTitle_WithNoTokens_IsReturnedUnchanged()
    {
        var result = QuickEntryParser.Parse("Buy milk", Reference);

        Assert.Equal("Buy milk", result.Text);
        Assert.Null(result.DueDate);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void SingleTag_IsExtractedAndStrippedFromText()
    {
        var result = QuickEntryParser.Parse("Submit report #finance", Reference);

        Assert.Equal("Submit report", result.Text);
        Assert.Equal(new[] { "finance" }, result.Tags);
    }

    [Fact]
    public void MultipleTags_AreAllExtracted()
    {
        var result = QuickEntryParser.Parse("Plan trip #travel #personal", Reference);

        Assert.Equal("Plan trip", result.Text);
        Assert.Equal(new[] { "travel", "personal" }, result.Tags);
    }

    [Fact]
    public void DuplicateTags_AreDeduplicatedCaseInsensitively()
    {
        var result = QuickEntryParser.Parse("Task #Work #work #WORK", Reference);
        Assert.Equal(new[] { "Work" }, result.Tags);
    }

    [Fact]
    public void TagInsideAWord_IsNotExtracted()
    {
        var result = QuickEntryParser.Parse("Research C#programming foo#bar", Reference);

        Assert.Equal("Research C#programming foo#bar", result.Text);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void HyphenatedTag_IsExtracted()
    {
        var result = QuickEntryParser.Parse("Fix bug #high-priority", Reference);
        Assert.Equal(new[] { "high-priority" }, result.Tags);
    }

    [Theory]
    [InlineData("!due:today", 2026, 3, 4)]
    [InlineData("!due:tomorrow", 2026, 3, 5)]
    public void DueToken_TodayAndTomorrow_ResolveRelativeToReferenceDate(string token, int year, int month, int day)
    {
        var result = QuickEntryParser.Parse($"Task {token}", Reference);

        Assert.Equal(new DateTime(year, month, day, 9, 0, 0), result.DueDate);
    }

    [Fact]
    public void DueToken_SameWeekdayAsReference_ResolvesToToday()
    {
        // Reference is a Wednesday.
        var result = QuickEntryParser.Parse("Task !due:wed", Reference);
        Assert.Equal(new DateTime(2026, 3, 4, 9, 0, 0), result.DueDate);
    }

    [Fact]
    public void DueToken_FutureWeekday_ResolvesToNearestUpcomingOccurrence()
    {
        // Reference is Wednesday March 4; the next Friday is March 6.
        var result = QuickEntryParser.Parse("Task !due:fri", Reference);
        Assert.Equal(new DateTime(2026, 3, 6, 9, 0, 0), result.DueDate);
    }

    [Fact]
    public void DueToken_PastWeekday_WrapsToNextWeek()
    {
        // Reference is Wednesday March 4; the next Monday is March 9, not March 2.
        var result = QuickEntryParser.Parse("Task !due:mon", Reference);
        Assert.Equal(new DateTime(2026, 3, 9, 9, 0, 0), result.DueDate);
    }

    [Fact]
    public void DueToken_FullWeekdayName_IsAlsoRecognized()
    {
        var result = QuickEntryParser.Parse("Task !due:friday", Reference);
        Assert.Equal(new DateTime(2026, 3, 6, 9, 0, 0), result.DueDate);
    }

    [Fact]
    public void DueToken_LiteralDate_IsParsed()
    {
        var result = QuickEntryParser.Parse("Task !due:12/25/2026", Reference);
        Assert.Equal(new DateTime(2026, 12, 25, 9, 0, 0), result.DueDate);
    }

    [Fact]
    public void DueToken_Unrecognized_IsLeftInTitleAndDoesNotSetDueDate()
    {
        var result = QuickEntryParser.Parse("Task !due:whenever", Reference);

        Assert.Equal("Task !due:whenever", result.Text);
        Assert.Null(result.DueDate);
    }

    [Theory]
    [InlineData("@3pm", 15, 0)]
    [InlineData("@3:30pm", 15, 30)]
    [InlineData("@9am", 9, 0)]
    [InlineData("@12am", 0, 0)]
    [InlineData("@12pm", 12, 0)]
    [InlineData("@15:30", 15, 30)]
    [InlineData("@09:05", 9, 5)]
    public void TimeToken_VariousFormats_AreParsedToTheCorrectHourAndMinute(string token, int hour, int minute)
    {
        var result = QuickEntryParser.Parse($"Task !due:today {token}", Reference);
        Assert.Equal(new DateTime(2026, 3, 4, hour, minute, 0), result.DueDate);
    }

    [Fact]
    public void TimeToken_WithoutDueToken_DefaultsDueDateToToday()
    {
        var result = QuickEntryParser.Parse("Call the bank @2pm", Reference);
        Assert.Equal(new DateTime(2026, 3, 4, 14, 0, 0), result.DueDate);
    }

    [Fact]
    public void DueTokenWithoutTimeToken_DefaultsToNineAm()
    {
        var result = QuickEntryParser.Parse("Task !due:tomorrow", Reference);
        Assert.Equal(new DateTime(2026, 3, 5, 9, 0, 0), result.DueDate);
    }

    [Fact]
    public void EmailAddress_IsNotMistakenForATimeToken()
    {
        var result = QuickEntryParser.Parse("Email john@example.com about the report", Reference);

        Assert.Equal("Email john@example.com about the report", result.Text);
        Assert.Null(result.DueDate);
    }

    [Fact]
    public void InvalidTimeToken_IsLeftInTitleAndDoesNotSetDueDate()
    {
        var result = QuickEntryParser.Parse("Task @25:99", Reference);

        Assert.Equal("Task @25:99", result.Text);
        Assert.Null(result.DueDate);
    }

    [Fact]
    public void CombinedTagsDueDateAndTime_AllParseTogetherFromOneString()
    {
        var result = QuickEntryParser.Parse("Submit budget report !due:tue @3pm #finance", Reference);

        Assert.Equal("Submit budget report", result.Text);
        Assert.Equal(new[] { "finance" }, result.Tags);
        // Reference is Wed Mar 4; next Tuesday is Mar 10.
        Assert.Equal(new DateTime(2026, 3, 10, 15, 0, 0), result.DueDate);
    }

    [Fact]
    public void ExtraWhitespaceLeftBehindByRemovedTokens_IsCollapsed()
    {
        var result = QuickEntryParser.Parse("Buy   milk  #groceries   !due:today", Reference);
        Assert.Equal("Buy milk", result.Text);
    }

    [Fact]
    public void TokensOnly_LeavesEmptyText()
    {
        var result = QuickEntryParser.Parse("#tag !due:today", Reference);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void NullInput_DoesNotThrowAndReturnsEmptyResult()
    {
        var result = QuickEntryParser.Parse(null!, Reference);

        Assert.Equal(string.Empty, result.Text);
        Assert.Null(result.DueDate);
        Assert.Empty(result.Tags);
    }

    // Plain-language phrases at the end of the title - same vectors as parity.test.js's
    // "natural language" cases. Due is "yyyy-MM-dd HH:mm", or null for no due date.
    [Theory]
    [InlineData("Call mom tomorrow 3pm", "Call mom", "2026-03-05 15:00", RecurrenceRule.None, 1)]
    [InlineData("Call mom tomorrow at 3:30pm", "Call mom", "2026-03-05 15:30", RecurrenceRule.None, 1)]
    [InlineData("Call mom tomorrow at 3 pm", "Call mom", "2026-03-05 15:00", RecurrenceRule.None, 1)]
    [InlineData("Pay rent every month", "Pay rent", "2026-03-04 09:00", RecurrenceRule.Monthly, 1)]
    [InlineData("Water plants every 2 weeks", "Water plants", "2026-03-04 09:00", RecurrenceRule.Weekly, 2)]
    [InlineData("Team sync every monday 10am", "Team sync", "2026-03-09 10:00", RecurrenceRule.Weekly, 1)]
    [InlineData("Dentist next fri", "Dentist", "2026-03-06 09:00", RecurrenceRule.None, 1)]
    [InlineData("Dentist next wed", "Dentist", "2026-03-11 09:00", RecurrenceRule.None, 1)]
    [InlineData("Dentist wed", "Dentist", "2026-03-04 09:00", RecurrenceRule.None, 1)]
    [InlineData("Report on friday", "Report", "2026-03-06 09:00", RecurrenceRule.None, 1)]
    [InlineData("Renew passport in 3 weeks", "Renew passport", "2026-03-25 09:00", RecurrenceRule.None, 1)]
    [InlineData("Renew passport in 10 days", "Renew passport", "2026-03-14 09:00", RecurrenceRule.None, 1)]
    [InlineData("Stand-up daily", "Stand-up", "2026-03-04 09:00", RecurrenceRule.Daily, 1)]
    [InlineData("Taxes yearly", "Taxes", "2026-03-04 09:00", RecurrenceRule.Yearly, 1)]
    [InlineData("Buy milk tonight", "Buy milk", "2026-03-04 20:00", RecurrenceRule.None, 1)]
    [InlineData("Buy milk tonight 7pm", "Buy milk", "2026-03-04 19:00", RecurrenceRule.None, 1)]
    [InlineData("Gym tomorrow 7am #health", "Gym", "2026-03-05 07:00", RecurrenceRule.None, 1)]
    [InlineData("Standup every day @9am", "Standup", "2026-03-04 09:00", RecurrenceRule.Daily, 1)]
    [InlineData("Lunch 12:30", "Lunch", "2026-03-04 12:30", RecurrenceRule.None, 1)]
    [InlineData("Call Tuesday about the budget", "Call Tuesday about the budget", null, RecurrenceRule.None, 1)]
    [InlineData("Tomorrow", "Tomorrow", null, RecurrenceRule.None, 1)]
    [InlineData("every day", "every day", null, RecurrenceRule.None, 1)]
    [InlineData("Sync every 2 mondays", "Sync every 2 mondays", null, RecurrenceRule.None, 1)]
    public void NaturalLanguage(string input, string title, string? due, RecurrenceRule recurrence, int interval)
    {
        var result = QuickEntryParser.Parse(input, Reference);

        Assert.Equal(title, result.Text);
        Assert.Equal(due is null ? null : DateTime.ParseExact(due, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), result.DueDate);
        Assert.Equal(recurrence, result.Recurrence);
        Assert.Equal(interval, result.RecurrenceInterval);
    }

    [Fact]
    public void ExplicitDueToken_WinsOverTrailingDatePhrase()
    {
        var result = QuickEntryParser.Parse("Plan trip tomorrow !due:fri", Reference);

        Assert.Equal("Plan trip tomorrow", result.Text);
        Assert.Equal(new DateTime(2026, 3, 6, 9, 0, 0), result.DueDate);
    }

    [Theory]
    [InlineData(RecurrenceRule.None, 1, null)]
    [InlineData(RecurrenceRule.Daily, 1, "repeats daily")]
    [InlineData(RecurrenceRule.Weekly, 1, "repeats weekly")]
    [InlineData(RecurrenceRule.Monthly, 3, "repeats every 3 months")]
    public void DescribeRecurrence_ReadsNaturally(RecurrenceRule rule, int interval, string? expected)
        => Assert.Equal(expected, QuickEntryParser.DescribeRecurrence(rule, interval));
}
