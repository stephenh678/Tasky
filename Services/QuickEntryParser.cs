using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TodoApp.Services;

public readonly record struct QuickEntryResult(string Text, DateTime? DueDate, List<string> Tags);

/// <summary>
/// Parses a small set of inline tokens out of quick-add task text: #tag for tags, !due:&lt;value&gt;
/// for a due date (today/tomorrow/a weekday name/a literal date), and @&lt;time&gt; for a time of
/// day - e.g. "Submit budget report !due:tue @3pm #finance".
///
/// Deliberately a fixed token syntax rather than full natural-language date parsing: free-text NLP
/// risks misreading plain words in a title (a task literally titled "Call Tuesday about the
/// budget" doesn't have a due date) and pulls in a large localization-heavy dependency for a
/// single-user desktop app. A token is only ever consumed when it actually matches one of these
/// forms, so an unrecognized "!due:whenever" or an email-address-shaped "@" is left untouched in
/// the title instead of silently mangled.
/// </summary>
public static class QuickEntryParser
{
    // (?<!\S) requires the token start with whitespace or the beginning of the string, so these
    // only fire on standalone tokens - "foo#bar" or "user@example.com" are left alone.
    private static readonly Regex TagPattern = new(@"(?<!\S)#([\w-]+)", RegexOptions.Compiled);
    private static readonly Regex DuePattern = new(@"(?<!\S)!due:(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TimePattern = new(@"(?<!\S)@(\S+)", RegexOptions.Compiled);

    private static readonly Regex TimeTokenPattern = new(
        @"^(?<hour>\d{1,2})(:(?<minute>\d{2}))?(?<meridiem>am|pm)$|^(?<hour24>\d{1,2}):(?<minute24>\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ROADMAP #62: was an inline Regex.Replace call, recompiling this pattern on every Parse call.
    private static readonly Regex MultiSpacePattern = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["weds"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
    };

    // Applied when !due: is given without an @ time - e.g. "!due:tomorrow" alone due for 9 AM
    // rather than midnight, so it doesn't look overdue the instant the day starts.
    private const int DefaultDueHour = 9;

    public static QuickEntryResult Parse(string input, DateTime? now = null)
    {
        var text = input ?? string.Empty;
        var reference = now ?? DateTime.Now;

        var tags = new List<string>();
        text = TagPattern.Replace(text, m =>
        {
            var tag = m.Groups[1].Value;
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);
            return string.Empty;
        });

        DateOnly? datePart = null;
        text = DuePattern.Replace(text, m =>
        {
            if (TryParseDueToken(m.Groups[1].Value, reference, out var parsed))
            {
                datePart = parsed;
                return string.Empty;
            }
            return m.Value; // unrecognized - leave it in the title rather than silently eating it
        });

        TimeOnly? timePart = null;
        text = TimePattern.Replace(text, m =>
        {
            if (TryParseTimeToken(m.Groups[1].Value, out var parsed))
            {
                timePart = parsed;
                return string.Empty;
            }
            return m.Value;
        });

        DateTime? dueDate = datePart is { } d
            ? d.ToDateTime(timePart ?? new TimeOnly(DefaultDueHour, 0))
            : timePart is { } t
                ? DateOnly.FromDateTime(reference).ToDateTime(t)
                : null;

        text = MultiSpacePattern.Replace(text, " ").Trim();

        return new QuickEntryResult(text, dueDate, tags);
    }

    private static bool TryParseDueToken(string token, DateTime reference, out DateOnly result)
    {
        result = default;

        if (token.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            result = DateOnly.FromDateTime(reference);
            return true;
        }

        if (token.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
        {
            result = DateOnly.FromDateTime(reference.AddDays(1));
            return true;
        }

        if (WeekdayNames.TryGetValue(token, out var weekday))
        {
            var today = DateOnly.FromDateTime(reference);
            // Nearest occurrence of that weekday, counting today as valid (so "!due:tue" typed
            // on a Tuesday means today, not a week out).
            var offset = ((int)weekday - (int)today.DayOfWeek + 7) % 7;
            result = today.AddDays(offset);
            return true;
        }

        return DateOnly.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool TryParseTimeToken(string token, out TimeOnly result)
    {
        result = default;
        var match = TimeTokenPattern.Match(token);
        if (!match.Success) return false;

        if (match.Groups["meridiem"].Success)
        {
            var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            var minute = match.Groups["minute"].Success
                ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture)
                : 0;
            if (hour is < 1 or > 12 || minute is < 0 or > 59) return false;

            var isPm = match.Groups["meridiem"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            var hour24 = hour % 12 + (isPm ? 12 : 0);
            result = new TimeOnly(hour24, minute);
            return true;
        }

        var hour24Value = int.Parse(match.Groups["hour24"].Value, CultureInfo.InvariantCulture);
        var minute24Value = int.Parse(match.Groups["minute24"].Value, CultureInfo.InvariantCulture);
        if (hour24Value is < 0 or > 23 || minute24Value is < 0 or > 59) return false;

        result = new TimeOnly(hour24Value, minute24Value);
        return true;
    }
}
