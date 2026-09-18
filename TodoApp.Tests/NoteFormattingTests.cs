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

    // Exact shapes docs/js/model.js htmlToXaml emits (see the matching cases in
    // docs/js/test/parity.test.js) - if desktop can't XamlReader.Parse one of these, every note
    // edited on the web in that shape loses its formatting on desktop.
    private const string Fd = "<FlowDocument xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TextAlignment=\"Left\">";

    [Theory]
    [InlineData(Fd + "<Paragraph><Run Text=\"first line\"/></Paragraph><Paragraph/><Paragraph><Run Text=\"a  b &amp; &quot;c&quot;\"/><LineBreak/><Run Text=\"next\"/></Paragraph></FlowDocument>")]
    [InlineData(Fd + "<Paragraph><Bold><Run Text=\"B\"/></Bold><Run Text=\" \"/><Italic><Run Text=\"I\"/></Italic><Underline><Run Text=\"U\"/></Underline><Hyperlink NavigateUri=\"https://example.com\"><Run Text=\"Link\"/></Hyperlink></Paragraph></FlowDocument>")]
    [InlineData(Fd + "<List><ListItem><Paragraph><Run Text=\"one\"/></Paragraph><List><ListItem><Paragraph><Run Text=\"nested\"/></Paragraph></ListItem></List></ListItem></List><List MarkerStyle=\"Decimal\"><ListItem><Paragraph/></ListItem></List></FlowDocument>")]
    [InlineData(Fd + "<Table><TableRowGroup><TableRow><TableCell><Paragraph><Run Text=\"c1\"/></Paragraph></TableCell><TableCell><Paragraph/></TableCell></TableRow></TableRowGroup></Table><Paragraph FontWeight=\"Bold\"><Run Text=\"Heading\"/></Paragraph></FlowDocument>")]
    [InlineData(Fd + "<Paragraph><InlineUIContainer><CheckBox IsChecked=\"True\" Margin=\"0,0,6,0\" VerticalAlignment=\"Center\" Cursor=\"Hand\"/></InlineUIContainer><Run Text=\"Milk\"/></Paragraph></FlowDocument>")]
    public void WebEditorXaml_ParsesOnDesktop(string xaml) => RunOnSta(() =>
    {
        var parsed = Assert.IsType<FlowDocument>(XamlReader.Parse(xaml));
        Assert.NotEmpty(parsed.Blocks);
    });

    [Fact]
    public void WebEditorXaml_KeepsSpacesBetweenRuns()
    {
        var parsed = Assert.IsType<FlowDocument>(XamlReader.Parse(
            Fd + "<Paragraph><Bold><Run Text=\"bold\"/></Bold><Run Text=\" and \"/><Italic><Run Text=\"italic\"/></Italic></Paragraph></FlowDocument>"));
        var text = new TextRange(parsed.ContentStart, parsed.ContentEnd).Text.TrimEnd('\r', '\n');
        Assert.Equal("bold and italic", text);
    }

    [Fact]
    public void WebEditorXaml_InlineCheckboxRoundTripsCheckedState() => RunOnSta(() =>
    {
        var parsed = Assert.IsType<FlowDocument>(XamlReader.Parse(
            Fd + "<Paragraph><InlineUIContainer><CheckBox IsChecked=\"True\" Margin=\"0,0,6,0\" VerticalAlignment=\"Center\" Cursor=\"Hand\"/></InlineUIContainer><Run Text=\"Milk\"/></Paragraph></FlowDocument>"));
        var para = Assert.IsType<Paragraph>(parsed.Blocks.FirstBlock);
        var box = Assert.IsType<System.Windows.Controls.CheckBox>(para.Inlines.OfType<InlineUIContainer>().Single().Child);
        Assert.True(box.IsChecked);
    });

    // CheckBox (like every Control) can only be constructed on an STA thread, which xunit's
    // worker threads aren't - the real app always parses on the UI thread.
    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
