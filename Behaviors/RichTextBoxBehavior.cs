using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TodoApp.Models;

namespace TodoApp.Behaviors;

/// <summary>
/// Wires a RichTextBox up to a NoteBlock's Text/Rtf fields, since RichTextBox.Document
/// does not support normal data binding. Setup happens on Loaded rather than a bound
/// DependencyProperty's changed-callback, because WPF skips that callback whenever the
/// new value equals the property's default (true for every brand-new, empty block) -
/// which silently meant TextChanged never got subscribed for empty starter blocks.
/// </summary>
public static class RichTextBoxBehavior
{
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached("Enable", typeof(bool), typeof(RichTextBoxBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    public static readonly DependencyProperty IsEmptyProperty =
        DependencyProperty.RegisterAttached("IsEmpty", typeof(bool), typeof(RichTextBoxBehavior), new PropertyMetadata(true));

    public static bool GetEnable(DependencyObject obj) => (bool)obj.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject obj, bool value) => obj.SetValue(EnableProperty, value);

    public static bool GetIsEmpty(DependencyObject obj) => (bool)obj.GetValue(IsEmptyProperty);
    public static void SetIsEmpty(DependencyObject obj, bool value) => obj.SetValue(IsEmptyProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb || e.NewValue is not true) return;
        rtb.Loaded += Rtb_Loaded;
    }

    private static void Rtb_Loaded(object sender, RoutedEventArgs e)
    {
        var rtb = (RichTextBox)sender;
        if (rtb.DataContext is NoteBlock block)
            LoadContent(rtb, block);
        rtb.TextChanged += Rtb_TextChanged;
    }

    private static void Rtb_TextChanged(object sender, TextChangedEventArgs e)
    {
        var rtb = (RichTextBox)sender;
        var plainText = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n');
        SetIsEmpty(rtb, plainText.Length == 0);

        if (rtb.DataContext is not NoteBlock block) return;
        block.Text = plainText;
        block.Rtf = SaveRtf(rtb);
    }

    private static void LoadContent(RichTextBox rtb, NoteBlock block)
    {
        rtb.Document.Blocks.Clear();

        if (!string.IsNullOrEmpty(block.Rtf))
        {
            var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(block.Rtf));
            try
            {
                range.Load(stream, DataFormats.Rtf);
                SetIsEmpty(rtb, new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0);
                return;
            }
            catch (ArgumentException)
            {
                // Corrupt RTF; fall through to seed from plain text below.
            }
        }

        if (block.Text.Length > 0)
            rtb.Document.Blocks.Add(new Paragraph(new Run(block.Text)));

        SetIsEmpty(rtb, new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0);
    }

    private static string SaveRtf(RichTextBox rtb)
    {
        var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Rtf);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
