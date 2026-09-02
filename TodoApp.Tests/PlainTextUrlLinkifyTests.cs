using System.Linq;
using TodoApp.Behaviors;

namespace TodoApp.Tests;

// Reproduces the actual bug report: pasting a chunk of plain text (no Html/Rtf on the clipboard -
// e.g. copied from Notepad, or a plain-text email/SMS view) that has a URL mixed in among other
// words left the URL as dead text. WPF's native RichTextBox.Paste() has no notion of "this
// substring is a URL" for plain text, and the pre-existing bare-URL check only fires when the
// *entire* clipboard is just a URL with nothing else - a sentence containing one fell through
// both.
public class PlainTextUrlLinkifyTests
{
    [Fact]
    public void ContainsEmbeddedUrl_TrueForUrlInsideSentence()
    {
        Assert.True(RichTextBoxBehavior.ContainsEmbeddedUrl("Check this out https://example.com/page please"));
    }

    [Fact]
    public void ContainsEmbeddedUrl_FalseForPlainSentence()
    {
        Assert.False(RichTextBoxBehavior.ContainsEmbeddedUrl("Just a normal note with no links in it."));
    }

    [Fact]
    public void ParsePlainTextUrlSegments_UrlMidSentence_SplitsIntoTextLinkText()
    {
        var segments = RichTextBoxBehavior.ParsePlainTextUrlSegments(
            "Check https://example.com/report.xlsx please");

        Assert.Equal(3, segments.Count);
        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Text, segments[0].Kind);
        Assert.Equal("Check ", segments[0].Text);

        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Link, segments[1].Kind);
        Assert.Equal("https://example.com/report.xlsx", segments[1].Text);
        Assert.Equal("https://example.com/report.xlsx", segments[1].Url);

        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Text, segments[2].Kind);
        Assert.Equal(" please", segments[2].Text);
    }

    [Fact]
    public void ParsePlainTextUrlSegments_WwwUrlWithoutScheme_LinksToHttps()
    {
        var segments = RichTextBoxBehavior.ParsePlainTextUrlSegments("see www.example.com for info");

        var link = segments.Single(s => s.Kind == RichTextBoxBehavior.HtmlSegmentKind.Link);
        Assert.Equal("www.example.com", link.Text);
        Assert.Equal("https://www.example.com", link.Url);
    }

    [Fact]
    public void ParsePlainTextUrlSegments_TrailingSentencePunctuation_NotIncludedInUrl()
    {
        var segments = RichTextBoxBehavior.ParsePlainTextUrlSegments(
            "Docs are at https://example.com/page.html, let me know.");

        var link = segments.Single(s => s.Kind == RichTextBoxBehavior.HtmlSegmentKind.Link);
        Assert.Equal("https://example.com/page.html", link.Text);

        var trailing = segments.Last();
        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Text, trailing.Kind);
        Assert.Equal(", let me know.", trailing.Text);
    }

    [Fact]
    public void ParsePlainTextUrlSegments_ParentheticalUrl_ClosingParenExcluded()
    {
        var segments = RichTextBoxBehavior.ParsePlainTextUrlSegments(
            "See the report (https://example.com/report.xlsx) for details");

        var link = segments.Single(s => s.Kind == RichTextBoxBehavior.HtmlSegmentKind.Link);
        Assert.Equal("https://example.com/report.xlsx", link.Text);
    }

    [Fact]
    public void ParsePlainTextUrlSegments_MultipleUrls_AllBecomeLinks()
    {
        var segments = RichTextBoxBehavior.ParsePlainTextUrlSegments(
            "First https://a.example.com then https://b.example.com done");

        Assert.Equal(2, segments.Count(s => s.Kind == RichTextBoxBehavior.HtmlSegmentKind.Link));
    }

    [Fact]
    public void ParsePlainTextUrlSegments_NoUrl_ReturnsSingleTextSegment()
    {
        var segments = RichTextBoxBehavior.ParsePlainTextUrlSegments("just plain text, nothing to link");

        var only = Assert.Single(segments);
        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Text, only.Kind);
        Assert.Equal("just plain text, nothing to link", only.Text);
    }

    [Fact]
    public void ParsePlainTextUrlSegments_UrlIsEntireLine_SingleLinkSegment()
    {
        var segments = RichTextBoxBehavior.ParsePlainTextUrlSegments("https://example.com");

        var only = Assert.Single(segments);
        Assert.Equal(RichTextBoxBehavior.HtmlSegmentKind.Link, only.Kind);
        Assert.Equal("https://example.com", only.Text);
    }
}
