using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TodoApp.Models;

namespace TodoApp.Services;

// Pure, testable search matching - kept separate from MainViewModel.FilterTask so it can be unit
// tested without constructing a full ViewModel (same rationale as QuickEntryParser/TaskSyncMerge).
// Mirrors Tasky Web's applySearch operator syntax (docs/js/app.js) exactly, so search behaves
// identically on both platforms: tag:name, is:overdue|pinned|recurring|done, has:link|attachment,
// due:today|week|none - all combinable with each other and with plain free text (e.g.
// "is:overdue milk"). Free text also matches checklist item text and attachment/link
// filename/URL/label, not just the task title and block text.
public static class TaskSearchMatcher
{
    private static readonly Regex OperatorRegex = new(@"\b(tag|is|has|due):(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Matches(TaskItem task, string query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return true;

        var operators = new List<(string Key, string Value)>();
        var freeText = OperatorRegex.Replace(trimmed, m =>
        {
            operators.Add((m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.ToLowerInvariant()));
            return string.Empty;
        }).Trim();

        foreach (var (key, value) in operators)
        {
            if (!MatchesOperator(task, key, value)) return false;
        }

        if (freeText.Length == 0) return true;
        return Contains(task.Text, freeText)
            || task.Body.Any(b => BlockMatches(b, freeText))
            || task.Tags.Any(tag => Contains(tag, freeText));
    }

    // FileName covers both Photo and File blocks (NoteBlock.FileName derives from PhotoPath for
    // both). ROADMAP.md #122: previously only block.Text and tags were searchable.
    private static bool BlockMatches(NoteBlock block, string freeText)
        => Contains(block.Text, freeText)
           || Contains(block.FileName, freeText)
           || Contains(block.LinkLabel, freeText)
           || Contains(block.Url, freeText)
           || block.ChecklistItems.Any(ci => Contains(ci.Text, freeText));

    private static bool MatchesOperator(TaskItem task, string key, string value) => key switch
    {
        "tag" => task.Tags.Any(tag => Contains(tag, value)),
        "is" => value switch
        {
            "overdue" => task.DueDate.HasValue && !task.IsDone && task.DueDate.Value.Date < DateTime.Today,
            "pinned" => task.IsPinned,
            "recurring" => task.Recurrence != RecurrenceRule.None,
            "done" => task.IsDone,
            // No dedicated "High Priority" quick-filter chip on Tasky Web (desktop-only), but the
            // operator itself is shared vocabulary so a desktop-saved view using it still matches
            // correctly wherever that synced view gets opened. Mirrored in Tasky Web's applySearch.
            "highpriority" => task.Priority == TaskPriority.High,
            // An operator whose value isn't recognized is treated as a no-op (matches everything)
            // rather than excluding every task - same as Tasky Web's applySearch, and safer than
            // silently hiding all results over a typo like "is:overdu".
            _ => true
        },
        "has" => value switch
        {
            "link" => TaskMediaHelper.HasLink(task),
            "attachment" => TaskMediaHelper.HasAttachment(task) || TaskMediaHelper.HasPhoto(task),
            _ => true
        },
        "due" => value switch
        {
            "today" => task.DueDate.HasValue && task.DueDate.Value.Date == DateTime.Today,
            "week" => task.DueDate.HasValue && task.DueDate.Value.Date >= DateTime.Today
                                             && task.DueDate.Value.Date <= DateTime.Today.AddDays(7),
            "none" => !task.DueDate.HasValue,
            _ => true
        },
        _ => true
    };

    private static bool Contains(string haystack, string needle)
        => (haystack ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase);
}
