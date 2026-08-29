using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using TodoApp.Models;

namespace TodoApp.Services;

public static class ExportService
{
    // Exports every open, non-trashed task with a due date as a calendar event, so they can be
    // imported as a one-time snapshot into Google Calendar/Outlook/Apple Calendar. A task whose
    // DueDate has no time component (still midnight - the common case, since most tasks are only
    // ever given a date) becomes an all-day event; one with an actual time (set via the
    // QuickAddWindow "@3pm" syntax, or a future time-of-day picker) becomes a 30-minute timed
    // event. Returns the number of events written, so the caller can tell the user if there was
    // nothing to export.
    public static int ExportToICalendar(IEnumerable<TaskItem> tasks, string filePath)
    {
        var withDueDates = tasks.Where(t => !t.IsClosed && t.DueDate.HasValue).ToList();

        var sb = new StringBuilder();
        AppendFolded(sb, "BEGIN:VCALENDAR");
        AppendFolded(sb, "VERSION:2.0");
        AppendFolded(sb, "PRODID:-//Tasky//Tasky Task Manager//EN");
        AppendFolded(sb, "CALSCALE:GREGORIAN");

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        foreach (var task in withDueDates)
        {
            var due = task.DueDate!.Value;
            var isAllDay = due.TimeOfDay == TimeSpan.Zero;
            var summary = task.IsDone ? $"[Done] {task.Text}" : task.Text;

            AppendFolded(sb, "BEGIN:VEVENT");
            // Stable across re-exports of the same task, so re-importing an updated .ics lets a
            // calendar app that supports it treat this as an update to the same event rather than
            // a duplicate.
            AppendFolded(sb, $"UID:{task.Id}@tasky.app");
            AppendFolded(sb, $"DTSTAMP:{stamp}");

            if (isAllDay)
            {
                // All-day events use an exclusive end date one day after start, per RFC 5545 -
                // without it, some calendar apps render a single-day all-day event as spanning
                // zero days.
                AppendFolded(sb, $"DTSTART;VALUE=DATE:{due:yyyyMMdd}");
                AppendFolded(sb, $"DTEND;VALUE=DATE:{due.AddDays(1):yyyyMMdd}");
            }
            else
            {
                AppendFolded(sb, $"DTSTART:{due:yyyyMMdd'T'HHmmss}");
                AppendFolded(sb, $"DTEND:{due.AddMinutes(30):yyyyMMdd'T'HHmmss}");
            }

            AppendFolded(sb, $"SUMMARY:{EscapeIcsText(summary)}");
            if (task.Tags.Count > 0)
                AppendFolded(sb, $"DESCRIPTION:{EscapeIcsText("Tags: " + string.Join(", ", task.Tags))}");

            AppendFolded(sb, "END:VEVENT");
        }

        AppendFolded(sb, "END:VCALENDAR");

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return withDueDates.Count;
    }

    // RFC 5545 §3.1 requires content lines folded at 75 octets, with continuation lines starting
    // with a single space - some calendar importers reject or mis-parse unfolded long lines (a
    // task title near the 500-char max, in particular). Folds by character count rather than
    // exact UTF-8 octet count: a reasonable approximation given task titles are typically ASCII,
    // and an occasional short line for multi-byte text is a cosmetic-only deviation, not a
    // parsing failure.
    private static void AppendFolded(StringBuilder sb, string line)
    {
        const int maxLineLength = 75;
        if (line.Length <= maxLineLength)
        {
            sb.Append(line).Append("\r\n");
            return;
        }

        sb.Append(line, 0, maxLineLength).Append("\r\n");
        var pos = maxLineLength;
        while (pos < line.Length)
        {
            var chunkLength = Math.Min(maxLineLength - 1, line.Length - pos); // -1 for the leading fold space
            sb.Append(' ').Append(line, pos, chunkLength).Append("\r\n");
            pos += chunkLength;
        }
    }

    // RFC 5545 §3.3.11 TEXT value escaping. Order matters - backslashes from the other
    // replacements must not themselves get re-escaped, hence backslash first.
    private static string EscapeIcsText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");
    }

    // ROADMAP.md #135: whole-list export, alongside the existing per-note ExportToMarkdown/
    // ExportToHtml above. Deliberately reads Body's plain-text mirror (NoteBlock.Text/
    // ChecklistItem.Text) directly instead of rendering every task's FlowDocument the way the
    // per-note export does - opening/rendering a RichTextBox per task just to export a snapshot of
    // everything would be both expensive and, for tasks never actually opened this session, more
    // machinery than a list-wide export needs. Formatting (bold/italic/etc. from Rtf) is lost as a
    // result; plain text, checklists, links, and photo/file references are not.
    public static void ExportAllToMarkdown(IEnumerable<TaskItem> tasks, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tasky Export");
        sb.AppendLine();
        sb.AppendLine($"Exported {DateTime.Now:MMMM d, yyyy 'at' h:mm tt}");
        sb.AppendLine();

        foreach (var task in tasks.Where(t => !t.IsClosed).OrderBy(t => t.IsDone).ThenBy(t => t.Text))
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## {(task.IsDone ? "[x] " : "")}{EscapeMarkdown(task.Text)}");
            sb.AppendLine();
            if (task.DueDate.HasValue)
                sb.AppendLine($"**Due Date:** {task.DueDate.Value:yyyy-MM-dd}  ");
            if (task.Tags.Count > 0)
                sb.AppendLine($"**Tags:** {string.Join(", ", task.Tags.Select(t => $"`{t}`"))}  ");
            sb.AppendLine($"**Status:** {(task.IsDone ? "Completed" : "Open")}  ");
            sb.AppendLine();

            AppendBodyAsMarkdown(sb, task);
            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    public static void ExportAllToHtml(IEnumerable<TaskItem> tasks, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <title>Tasky Export</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; max-width: 800px; margin: 40px auto; padding: 0 20px; color: #2C3338; background: #FFF; }");
        sb.AppendLine("    h1 { font-size: 26px; margin-bottom: 4px; color: #1E2327; }");
        sb.AppendLine("    .exported-at { font-size: 13px; color: #646970; margin-bottom: 24px; }");
        sb.AppendLine("    h2 { font-size: 19px; margin: 28px 0 4px; padding-top: 20px; border-top: 1px solid #E2E4E7; color: #1E2327; }");
        sb.AppendLine("    h2.done { color: #8C8F94; text-decoration: line-through; }");
        sb.AppendLine("    .meta { font-size: 13px; color: #646970; margin-bottom: 10px; }");
        sb.AppendLine("    .tag { display: inline-block; background: #F0F0F1; padding: 2px 8px; border-radius: 12px; font-size: 11.5px; margin-right: 6px; }");
        sb.AppendLine("    .content p { margin: 6px 0; }");
        sb.AppendLine("    .checklist-item { display: flex; align-items: center; margin: 3px 0; }");
        sb.AppendLine("    .checklist-item input { margin-right: 8px; }");
        sb.AppendLine("    .attachment-ref { color: #646970; font-style: italic; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Tasky Export</h1>");
        sb.AppendLine($"  <div class=\"exported-at\">Exported {DateTime.Now:MMMM d, yyyy 'at' h:mm tt}</div>");

        foreach (var task in tasks.Where(t => !t.IsClosed).OrderBy(t => t.IsDone).ThenBy(t => t.Text))
        {
            sb.AppendLine($"  <h2 class=\"{(task.IsDone ? "done" : "")}\">{Escape(task.Text)}</h2>");
            sb.AppendLine("  <div class=\"meta\">");
            if (task.DueDate.HasValue)
                sb.AppendLine($"    <div><strong>Due Date:</strong> {task.DueDate.Value:MMMM d, yyyy}</div>");
            if (task.Tags.Count > 0)
            {
                sb.Append("    <div style=\"margin-top:4px;\"><strong>Tags:</strong> ");
                foreach (var tag in task.Tags)
                    sb.Append($"<span class=\"tag\">{Escape(tag)}</span>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("  </div>");
            sb.AppendLine("  <div class=\"content\">");
            AppendBodyAsHtml(sb, task);
            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static void AppendBodyAsMarkdown(StringBuilder sb, TaskItem task)
    {
        foreach (var block in task.Body)
        {
            switch (block.Type)
            {
                case NoteBlockType.Text:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        sb.AppendLine(EscapeMarkdown(block.Text));
                        sb.AppendLine();
                    }
                    break;
                case NoteBlockType.Checklist:
                    foreach (var item in block.ChecklistItems)
                        sb.AppendLine($"- [{(item.IsChecked ? "x" : " ")}] {EscapeMarkdown(item.Text)}");
                    sb.AppendLine();
                    break;
                case NoteBlockType.Link:
                    sb.AppendLine($"[{EscapeMarkdown(string.IsNullOrEmpty(block.LinkLabel) ? block.Url : block.LinkLabel)}]({block.Url})");
                    sb.AppendLine();
                    break;
                case NoteBlockType.Photo:
                case NoteBlockType.File:
                    sb.AppendLine($"*Attachment: {EscapeMarkdown(block.FileName)}*");
                    sb.AppendLine();
                    break;
            }
        }
    }

    private static void AppendBodyAsHtml(StringBuilder sb, TaskItem task)
    {
        foreach (var block in task.Body)
        {
            switch (block.Type)
            {
                case NoteBlockType.Text:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                        sb.AppendLine($"    <p>{Escape(block.Text)}</p>");
                    break;
                case NoteBlockType.Checklist:
                    foreach (var item in block.ChecklistItems)
                        sb.AppendLine($"    <div class=\"checklist-item\"><input type=\"checkbox\" {(item.IsChecked ? "checked disabled" : "disabled")}/> <span>{Escape(item.Text)}</span></div>");
                    break;
                case NoteBlockType.Link:
                    var label = string.IsNullOrEmpty(block.LinkLabel) ? block.Url : block.LinkLabel;
                    sb.AppendLine($"    <p><a href=\"{Escape(block.Url)}\">{Escape(label)}</a></p>");
                    break;
                case NoteBlockType.Photo:
                case NoteBlockType.File:
                    sb.AppendLine($"    <p class=\"attachment-ref\">Attachment: {Escape(block.FileName)}</p>");
                    break;
            }
        }
    }

    public static void ExportToHtml(TaskItem task, FlowDocument document, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine($"  <title>{Escape(task.Text)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; max-width: 800px; margin: 40px auto; padding: 0 20px; color: #2C3338; background: #FFF; }");
        sb.AppendLine("    h1 { font-size: 28px; margin-bottom: 8px; color: #1E2327; }");
        sb.AppendLine("    .meta { font-size: 13px; color: #646970; margin-bottom: 24px; padding-bottom: 12px; border-bottom: 1px solid #E2E4E7; }");
        sb.AppendLine("    .tag { display: inline-block; background: #F0F0F1; padding: 2px 8px; border-radius: 12px; font-size: 11.5px; margin-right: 6px; }");
        sb.AppendLine("    .content { font-size: 15px; }");
        sb.AppendLine("    .content p { margin: 8px 0; }");
        sb.AppendLine("    .content img { max-width: 100%; height: auto; border-radius: 6px; margin: 12px 0; }");
        sb.AppendLine("    table { border-collapse: collapse; width: 100%; margin: 16px 0; }");
        sb.AppendLine("    th, td { border: 1px solid #DCDCDE; padding: 8px 12px; text-align: left; }");
        sb.AppendLine("    th { background: #F6F7F7; font-weight: 600; }");
        sb.AppendLine("    .checklist-item { display: flex; align-items: center; margin: 4px 0; }");
        sb.AppendLine("    .checklist-item input { margin-right: 8px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine($"  <h1>{Escape(task.Text)}</h1>");
        sb.AppendLine("  <div class=\"meta\">");
        if (task.DueDate.HasValue)
            sb.AppendLine($"    <div><strong>Due Date:</strong> {task.DueDate.Value:MMMM d, yyyy}</div>");
        if (task.Tags.Count > 0)
        {
            sb.Append("    <div style=\"margin-top:4px;\"><strong>Tags:</strong> ");
            foreach (var tag in task.Tags)
                sb.Append($"<span class=\"tag\">{Escape(tag)}</span>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine($"    <div style=\"margin-top:4px;\"><strong>Status:</strong> {(task.IsDone ? "Completed" : "Open")}</div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <div class=\"content\">");

        foreach (var block in document.Blocks)
        {
            if (block is Paragraph p)
            {
                var hasCheckbox = p.Inlines.OfType<InlineUIContainer>().Any(c => c.Child is CheckBox);
                if (hasCheckbox)
                {
                    var cb = p.Inlines.OfType<InlineUIContainer>().First().Child as CheckBox;
                    var isChecked = cb?.IsChecked == true;
                    var text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    sb.AppendLine($"    <div class=\"checklist-item\"><input type=\"checkbox\" {(isChecked ? "checked disabled" : "disabled")}/> <span>{Escape(text)}</span></div>");
                }
                else
                {
                    var text = new TextRange(p.ContentStart, p.ContentEnd).Text.TrimEnd('\r', '\n');
                    if (string.IsNullOrWhiteSpace(text))
                        sb.AppendLine("    <p><br/></p>");
                    else
                        sb.AppendLine($"    <p>{Escape(text)}</p>");
                }
            }
            else if (block is BlockUIContainer bui && bui.Child is Border b && b.Child is Image img && img.Source is System.Windows.Media.Imaging.BitmapSource bmp)
            {
                var b64 = TryEncodeImageAsBase64Png(bmp);
                if (b64 is not null)
                    sb.AppendLine($"    <img src=\"data:image/png;base64,{b64}\" alt=\"Embedded Image\"/>");
            }
            else if (block is Table table)
            {
                sb.AppendLine("    <table>");
                foreach (var group in table.RowGroups)
                {
                    foreach (var row in group.Rows)
                    {
                        sb.AppendLine("      <tr>");
                        foreach (var cell in row.Cells)
                        {
                            var cellText = new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim();
                            sb.AppendLine($"        <td>{Escape(cellText)}</td>");
                        }
                        sb.AppendLine("      </tr>");
                    }
                }
                sb.AppendLine("    </table>");
            }
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    public static void ExportToMarkdown(TaskItem task, FlowDocument document, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {EscapeMarkdown(task.Text)}");
        sb.AppendLine();
        if (task.DueDate.HasValue)
            sb.AppendLine($"**Due Date:** {task.DueDate.Value:yyyy-MM-dd}  ");
        if (task.Tags.Count > 0)
            sb.AppendLine($"**Tags:** {string.Join(", ", task.Tags.Select(t => $"`{t}`"))}  ");
        sb.AppendLine($"**Status:** {(task.IsDone ? "Completed" : "Open")}  ");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var block in document.Blocks)
        {
            if (block is Paragraph p)
            {
                var cbContainer = p.Inlines.OfType<InlineUIContainer>().FirstOrDefault(c => c.Child is CheckBox);
                if (cbContainer is not null)
                {
                    var cb = cbContainer.Child as CheckBox;
                    var text = new TextRange(cbContainer.ElementEnd, p.ContentEnd).Text.Trim();
                    sb.AppendLine($"- [{(cb?.IsChecked == true ? "x" : " ")}] {EscapeMarkdown(text)}");
                }
                else
                {
                    var text = new TextRange(p.ContentStart, p.ContentEnd).Text.TrimEnd('\r', '\n');
                    sb.AppendLine(EscapeMarkdown(text));
                    sb.AppendLine();
                }
            }
            else if (block is BlockUIContainer bui && bui.Child is Border b && b.Child is Image img && img.Source is System.Windows.Media.Imaging.BitmapSource bmp)
            {
                var b64 = TryEncodeImageAsBase64Png(bmp);
                if (b64 is not null)
                {
                    sb.AppendLine($"![Embedded Image](data:image/png;base64,{b64})");
                    sb.AppendLine();
                }
            }
            else if (block is Table table)
            {
                foreach (var group in table.RowGroups)
                {
                    var isFirstRow = true;
                    foreach (var row in group.Rows)
                    {
                        var cells = row.Cells.Select(c => EscapeMarkdown(new TextRange(c.ContentStart, c.ContentEnd).Text.Trim())).ToList();
                        sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                        if (isFirstRow)
                        {
                            sb.AppendLine("| " + string.Join(" | ", cells.Select(_ => "---")) + " |");
                            isFirstRow = false;
                        }
                    }
                }
                sb.AppendLine();
            }
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    // Shared by ExportToHtml and ExportToMarkdown so both embed inline photos the same way -
    // returns null (and lets the caller skip the image) rather than throwing, since one
    // unencodable image shouldn't abort the rest of the export.
    private static string? TryEncodeImageAsBase64Png(System.Windows.Media.Imaging.BitmapSource bmp)
    {
        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (ArgumentException ex)
        {
            // Invalid image path or format
            System.Diagnostics.Debug.WriteLine($"Failed to load image: {ex.Message}");
            return null;
        }
    }

    public static void PrintDocument(FlowDocument document, string title)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() == true)
        {
            var docCopy = CloneDocument(document);
            docCopy.PageWidth = printDialog.PrintableAreaWidth;
            docCopy.PageHeight = printDialog.PrintableAreaHeight;
            docCopy.PagePadding = new System.Windows.Thickness(40);
            docCopy.ColumnWidth = double.PositiveInfinity; // single column printing

            var paginator = ((IDocumentPaginatorSource)docCopy).DocumentPaginator;
            printDialog.PrintDocument(paginator, title);
        }
    }

    private static FlowDocument CloneDocument(FlowDocument source)
    {
        var copy = new FlowDocument();
        using var stream = new MemoryStream();
        var sourceRange = new TextRange(source.ContentStart, source.ContentEnd);
        sourceRange.Save(stream, System.Windows.DataFormats.XamlPackage);
        var copyRange = new TextRange(copy.ContentStart, copy.ContentEnd);
        copyRange.Load(stream, System.Windows.DataFormats.XamlPackage);
        return copy;
    }

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

    // Only escapes characters that can actually be misread as Markdown syntax mid-text (emphasis,
    // code spans, links, table delimiters) - deliberately doesn't escape every punctuation mark
    // CommonMark technically allows escaping (e.g. '.', '-', '!'), since doing that to ordinary
    // task text would make the exported file far noisier than the actual formatting risk warrants.
    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '|')
                sb.Append('\\');
            sb.Append(c);
        }
        // A leading '#' turns the whole line into a heading - only matters at the very start, so
        // handle it separately rather than escaping every '#' anywhere in the text.
        return sb.Length > 0 && sb[0] == '#' ? "\\" + sb : sb.ToString();
    }
}
