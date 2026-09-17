using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using TodoApp.Behaviors;
using Xunit;

namespace TodoApp.Tests;

public class NoteFormattingTests
{
    [Fact]
    public void PopulateDocumentFromFormattedText_ParsesParagraphsAndMarkdown()
    {
        var doc = new FlowDocument();
        var markdownText = "First paragraph with **bold text** and *italic text*.\n\nSecond paragraph with [Tasky Website](https://tasky.app).";

        RichTextBoxBehavior.PopulateDocumentFromFormattedText(doc, markdownText);

        Assert.Equal(2, doc.Blocks.Count);

        var firstPara = Assert.IsType<Paragraph>(doc.Blocks.FirstBlock);
        Assert.Contains(firstPara.Inlines, inline => inline is Bold);
        Assert.Contains(firstPara.Inlines, inline => inline is Italic);

        var secondPara = Assert.IsType<Paragraph>(doc.Blocks.ElementAt(1));
        var hyperlink = Assert.Single(secondPara.Inlines.OfType<Hyperlink>());
        Assert.Equal("https://tasky.app/", hyperlink.NavigateUri.ToString());
    }

    [Fact]
    public void PopulateDocumentFromFormattedText_ParsesBulletLists()
    {
        var doc = new FlowDocument();
        var listText = "- First item\n- Second item with **bold**\n- Third item";

        RichTextBoxBehavior.PopulateDocumentFromFormattedText(doc, listText);

        var list = Assert.IsType<List>(doc.Blocks.FirstBlock);
        Assert.Equal(3, list.ListItems.Count);

        var secondItemPara = Assert.IsType<Paragraph>(list.ListItems.ElementAt(1).Blocks.FirstBlock);
        Assert.Contains(secondItemPara.Inlines, inline => inline is Bold);
    }

    [Fact]
    public void FlowDocumentFromWebXaml_ParsesCleanlyIntoWpfDocument()
    {
        var webGeneratedXaml = "<FlowDocument xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TextAlignment=\"Left\"><Paragraph>Hello <Bold>World</Bold></Paragraph></FlowDocument>";

        var parsed = XamlReader.Parse(webGeneratedXaml) as FlowDocument;
        Assert.NotNull(parsed);

        var para = Assert.IsType<Paragraph>(parsed.Blocks.FirstBlock);
        Assert.Contains(para.Inlines, inline => inline is Bold);
    }
}
