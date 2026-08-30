using System.Text.RegularExpressions;

namespace TodoApp.Services;

public static class TagUtils
{
    private static readonly Regex InvalidTagCharsPattern = new("[^\\w-]", RegexOptions.Compiled);

    // Matches Tasky Web's addTag()/normalizeTagName() (docs/js/app.js) so a tag typed on either
    // platform normalizes to the same string - extracted from TaskDetailViewModel's own SanitizeTag
    // so the bulk-edit tag dialog (which isn't tied to a single task's TaskDetailViewModel) can share
    // the exact same rule instead of a third near-identical copy.
    public static string Sanitize(string raw) => InvalidTagCharsPattern.Replace(raw.Trim().TrimStart('#'), "").ToLowerInvariant();
}
