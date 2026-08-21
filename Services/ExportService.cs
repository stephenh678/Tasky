using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using TodoApp.Models;

namespace TodoApp.Services;

public static class ExportService
{
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
