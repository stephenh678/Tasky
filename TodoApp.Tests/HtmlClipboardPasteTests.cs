using System.Linq;
using TodoApp.Behaviors;

namespace TodoApp.Tests;

public class HtmlClipboardPasteTests
{
    [Fact]
    public void ExtractHtmlFragment_UsesStartAndEndFragmentComments()
    {
        var cfHtml =
            "Version:0.9\r\n" +
            "StartHTML:0000000097\r\n" +
            "EndHTML:0000000200\r\n" +
            "StartFragment:0000000133\r\n" +
            "EndFragment:0000000160\r\n" +
            "<html><body>\r\n" +
            "<!--StartFragment--><p>hello</p><!--EndFragment-->\r\n" +
            "</body></html>";

        var fragment = RichTextBoxBehavior.ExtractHtmlFragment(cfHtml);

        Assert.Equal("<p>hello</p>", fragment);
    }

    [Fact]
    public void ExtractHtmlFragment_FallsBackToBody_WhenFragmentMarkersMissing()
    {
        var cfHtml = "<html><head><style>.x{}</style></head><body><p>hi</p></body></html>";

        var fragment = RichTextBoxBehavior.ExtractHtmlFragment(cfHtml);

        Assert.Equal("<p>hi</p>", fragment);
    }

    [Fact]
    public void ParseHtmlSegments_PlainAnchor_BecomesLinkSegment()
    {
        var segments = RichTextBoxBehavior.ParseHtmlSegments(
            "Check <a href=\"https://example.com/report.xlsx\">the report</a> please");

        Assert.Equal(3, segments.Count);
        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Text, segments[0].Kind);
        Assert.Equal("Check", segments[0].Text);

        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Link, segments[1].Kind);
        Assert.Equal("the report", segments[1].Text);
        Assert.Equal("https://example.com/report.xlsx", segments[1].Url);

        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Text, segments[2].Kind);
        Assert.Equal("please", segments[2].Text);
    }

    [Fact]
    public void ParseHtmlSegments_Image_BecomesImageSegmentNotRawText()
    {
        var segments = RichTextBoxBehavior.ParseHtmlSegments(
            "<img src=\"https://files.slack.com/files-pri/T1-F1/image.png\" alt=\"image.png\">");

        var image = Assert.Single(segments);
        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Image, image.Kind);
        Assert.Equal("image.png", image.Text);
        Assert.Equal("https://files.slack.com/files-pri/T1-F1/image.png", image.Url);
    }

    [Fact]
    public void ParseHtmlSegments_ImageWithoutAlt_FallsBackToFileNameFromUrl()
    {
        var segments = RichTextBoxBehavior.ParseHtmlSegments(
            "<img src=\"https://files.slack.com/files-pri/T1-F1/image.png?pub_secret=abc\">");

        var image = Assert.Single(segments);
        Assert.Equal("image.png", image.Text);
    }

    // Reproduces the actual bug report: pasting a Slack "copy message" (a text note, a real
    // hyperlink to a shared file, an inline image the clipboard can only reference by URL, and a
    // trailing sentence) used to collapse to garbled plain text - "Resurfacing public link to
    // edit here:  OTS Field TA - Q4 Regional POD Sign-Ups.xlsximage.png" - because WPF's native
    // RichTextBox.Paste() ignores the Html clipboard format entirely and falls back to Slack's
    // plain-text rendition. This asserts the link survives as a real Link segment (not flattened
    // text) and the image becomes its own Image segment (not text jammed against the link).
    [Fact]
    public void ParseHtmlSegments_SlackMessageShape_KeepsLinkAndImageDistinctFromSurroundingText()
    {
        var html =
            "<div>Resurfacing public link to edit here: " +
            "<a href=\"https://example.sharepoint.com/OTS%20Field%20TA%20-%20Q4%20Regional%20POD%20Sign-Ups.xlsx\">" +
            "OTS Field TA - Q4 Regional POD Sign-Ups.xlsx</a></div>" +
            "<div><img src=\"https://files.slack.com/files-pri/T1-F1/image.png\" alt=\"image.png\">" +
            "<span class=\"timestamp\">11:10 AM</span></div>" +
            "<div>Don't worry, sending Leon same note lol</div>";

        var segments = RichTextBoxBehavior.ParseHtmlSegments(html);

        var link = segments.Single(s => s.Kind == RichTextBoxBehavior.HtmlSegmentKind.Link);
        Assert.Equal("OTS Field TA - Q4 Regional POD Sign-Ups.xlsx", link.Text);
        Assert.Equal("https://example.sharepoint.com/OTS%20Field%20TA%20-%20Q4%20Regional%20POD%20Sign-Ups.xlsx", link.Url);

        var image = segments.Single(s => s.Kind == RichTextBoxBehavior.HtmlSegmentKind.Image);
        Assert.Equal("image.png", image.Text);

        // The image's filename/label must never end up glued onto the link's display text or the
        // preceding sentence - that concatenation (no separating space) was the literal symptom.
        Assert.DoesNotContain(segments, s => s.Text.Contains("Sign-Ups.xlsximage.png"));

        var trailing = segments.Last(s => s.Kind == RichTextBoxBehavior.HtmlSegmentKind.Text);
        Assert.Contains("Don't worry, sending Leon same note lol", trailing.Text);
    }

    [Fact]
    public void HtmlFragmentToPlainText_DecodesEntitiesAndNormalizesLineBreaks()
    {
        var text = RichTextBoxBehavior.HtmlFragmentToPlainText(
            "<p>Q1 &amp; Q2</p><p>Tom &amp; Jerry&nbsp;said &quot;hi&quot;</p>");

        Assert.Equal("Q1 & Q2\nTom & Jerry said \"hi\"", text);
    }

    [Fact]
    public void HtmlFragmentToPlainText_TableCells_AreSeparatedNotConcatenated()
    {
        var text = RichTextBoxBehavior.HtmlFragmentToPlainText(
            "<table><tr><td>Subregion</td><td>Core</td></tr><tr><td>MidAtlantic</td><td>15</td></tr></table>");

        Assert.Equal("Subregion | Core\nMidAtlantic | 15", text);
    }

    [Fact]
    public void ParseHtmlSegments_EmptyFragment_ReturnsNoSegments()
    {
        var segments = RichTextBoxBehavior.ParseHtmlSegments("   ");

        Assert.Empty(segments);
    }
}
