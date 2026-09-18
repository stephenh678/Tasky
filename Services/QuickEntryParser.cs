using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TodoApp.Models;

namespace TodoApp.Services;

public readonly record struct QuickEntryResult(
    string Text,
    DateTime? DueDate,
    List<string> Tags,
    RecurrenceRule Recurrence = RecurrenceRule.None,
    int RecurrenceInterval = 1);

/// <summary>
/// Parses a small set of inline tokens out of quick-add task text: #tag for tags, !due:&lt;value&gt;
/// for a due date (today/tomorrow/a weekday name/a literal date), and @&lt;time&gt; for a time of
/// day - e.g. "Submit budget report !due:tue @3pm #finance". A token is only ever consumed when it
/// actually matches one of these forms, so an unrecognized "!due:whenever" or an
/// email-address-shaped "@" is left untouched in the title instead of silently mangled.
///
/// On top of the tokens, a small set of plain-language phrases is read from the END of the title
/// only ("Call mom tomorrow 3pm", "Pay rent every month") - see ParseNaturalTail. Restricting it to
/// the tail is what keeps ordinary words safe: "Call Tuesday about the budget" has no due date.
/// Mirrored exactly by docs/js/model.js parseNaturalTail; the two test suites share vectors.
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

        text = MultiSpacePattern.Replace(text, " ").Trim();

        var natural = ParseNaturalTail(text, reference, needDate: datePart is null, needTime: timePart is null);
        text = natural.Text;
        datePart ??= natural.Date;
        timePart ??= natural.Time;

        DateTime? dueDate = datePart is { } d
            ? d.ToDateTime(timePart ?? new TimeOnly(natural.DefaultHour, 0))
            : timePart is { } t
                ? DateOnly.FromDateTime(reference).ToDateTime(t)
                : null;

        return new QuickEntryResult(text, dueDate, tags, natural.Recurrence, natural.RecurrenceInterval);
    }

    /// <summary>
    /// Preview wording for a parsed repeat ("repeats daily", "repeats every 2 weeks"), or null for
    /// none - shared by the two quick-add previews (QuickAddWindow and MainWindow's inline box).
    /// </summary>
    public static string? DescribeRecurrence(RecurrenceRule rule, int interval)
    {
        var unit = rule switch
        {
            RecurrenceRule.Daily => "day",
            RecurrenceRule.Weekly => "week",
            RecurrenceRule.Monthly => "month",
            RecurrenceRule.Yearly => "year",
            _ => null,
        };
        if (unit is null) return null;
        if (interval > 1) return $"repeats every {interval} {unit}s";
        return rule == RecurrenceRule.Daily ? "repeats daily" : $"repeats {unit}ly";
    }

    // --- Plain-language tail -------------------------------------------------------------------
    //   time:   3pm · 3:30 pm · 15:00 · at 9am
    //   date:   today · tonight (8 PM unless a time is given) · tomorrow/tmrw · [on|due] fri ·
    //           next fri (never today) · in 3 days · in 2 weeks
    //   repeat: daily · weekly · monthly · yearly · every day|week|month|year · every 2 weeks ·
    //           every monday (weekly, starting that day)
    // Phrases may stack in any order; one is never consumed if it would leave the title empty. A
    // repeat with no date starts today, so the series has an anchor.
    private const string NaturalWeekdays = "sunday|monday|tuesday|tues|tue|wednesday|weds|wed|thursday|thurs|thur|thu|friday|fri|saturday|sat|sun|mon";
    private const RegexOptions NaturalOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex NaturalTimeTail = new(
        @"\s(?:at\s+)?([0-9]{1,2}(?::[0-9]{2})?\s?(?:am|pm)|[0-9]{1,2}:[0-9]{2})$", NaturalOptions);
    private static readonly Regex NaturalRepeatTail = new(
        @"\s(?:every\s+(?:([0-9]{1,2})\s+)?(days?|weeks?|months?|years?|" + NaturalWeekdays + @")|(daily|weekly|monthly|yearly))$", NaturalOptions);
    private static readonly Regex NaturalDateTail = new(
        @"\s(?:(?:on|due)\s+)?(today|tonight|tomorrow|tmrw|next\s+(" + NaturalWeekdays + @")|(" + NaturalWeekdays + @")|in\s+([0-9]{1,3})\s+(days?|weeks?))$", NaturalOptions);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private const int TonightHour = 20;

    private readonly record struct NaturalTail(
        string Text, DateOnly? Date, TimeOnly? Time, RecurrenceRule Recurrence, int RecurrenceInterval, int DefaultHour);

    private static int WeekdayOffset(DateOnly today, string name, bool excludeToday = false)
    {
        var offset = ((int)WeekdayNames[name] - (int)today.DayOfWeek + 7) % 7;
        return offset == 0 && excludeToday ? 7 : offset;
    }

    private static NaturalTail ParseNaturalTail(string input, DateTime reference, bool needDate, bool needTime)
    {
        var text = input;
        DateOnly? date = null;
        TimeOnly? time = null;
        var recurrence = RecurrenceRule.None;
        var interval = 1;
        var defaultHour = DefaultDueHour;
        string? repeatWeekday = null;
        var today = DateOnly.FromDateTime(reference);

        Match? Take(Regex pattern)
        {
            var m = pattern.Match(text);
            return m.Success && text[..m.Index].Trim().Length > 0 ? m : null;
        }

        var changed = true;
        for (var guard = 0; changed && guard < 4; guard++)
        {
            changed = false;
            Match? m;
            if (needTime && time is null && (m = Take(NaturalTimeTail)) is not null
                && TryParseTimeToken(WhitespacePattern.Replace(m.Groups[1].Value, ""), out var parsedTime))
            {
                time = parsedTime;
                text = text[..m.Index].TrimEnd();
                changed = true;
                continue;
            }

            if (recurrence == RecurrenceRule.None && (m = Take(NaturalRepeatTail)) is not null)
            {
                var hasCount = m.Groups[1].Success;
                var word = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToLowerInvariant();
                var isWeekday = WeekdayNames.ContainsKey(word);
                if (!(isWeekday && hasCount))
                {
                    var unit = m.Groups[3].Success
                        ? word switch { "daily" => "day", "weekly" => "week", "monthly" => "month", _ => "year" }
                        : word.TrimEnd('s');
                    recurrence = isWeekday ? RecurrenceRule.Weekly : unit switch
                    {
                        "day" => RecurrenceRule.Daily,
                        "week" => RecurrenceRule.Weekly,
                        "month" => RecurrenceRule.Monthly,
                        _ => RecurrenceRule.Yearly,
                    };
                    interval = hasCount ? Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 1, 30) : 1;
                    if (isWeekday) repeatWeekday = word;
                    text = text[..m.Index].TrimEnd();
                    changed = true;
                    continue;
                }
            }

            if (needDate && date is null && (m = Take(NaturalDateTail)) is not null)
            {
                var phrase = m.Groups[1].Value.ToLowerInvariant();
                if (phrase == "today") date = today;
                else if (phrase == "tonight") { date = today; defaultHour = TonightHour; }
                else if (phrase is "tomorrow" or "tmrw") date = today.AddDays(1);
                else if (m.Groups[2].Success) date = today.AddDays(WeekdayOffset(today, m.Groups[2].Value, excludeToday: true));
                else if (m.Groups[3].Success) date = today.AddDays(WeekdayOffset(today, m.Groups[3].Value));
                else
                {
                    var count = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                    date = today.AddDays(count * (m.Groups[5].Value.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? 7 : 1));
                }
                text = text[..m.Index].TrimEnd();
                changed = true;
            }
        }

        if (recurrence != RecurrenceRule.None && needDate && date is null)
            date = repeatWeekday is null ? today : today.AddDays(WeekdayOffset(today, repeatWeekday));

        return new NaturalTail(text, date, time, recurrence, interval, defaultHour);
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
