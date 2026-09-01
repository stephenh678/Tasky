using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoApp.Models;
using TodoApp.Services;

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
        if (d is not RichTextBox rtb) return;
        if (e.NewValue is true)
        {
            rtb.Loaded -= Rtb_Loaded;
            rtb.Loaded += Rtb_Loaded;
            rtb.Unloaded -= Rtb_Unloaded;
            rtb.Unloaded += Rtb_Unloaded;
            rtb.DataContextChanged -= Rtb_DataContextChanged;
            rtb.DataContextChanged += Rtb_DataContextChanged;
            rtb.PreviewKeyDown -= Rtb_PreviewKeyDown;
            rtb.PreviewKeyDown += Rtb_PreviewKeyDown;
            if (rtb.IsLoaded && rtb.DataContext is NoteBlock block)
            {
                rtb.TextChanged -= Rtb_TextChanged;
                LoadContent(rtb, block);
                rtb.TextChanged += Rtb_TextChanged;
            }
        }
        else
        {
            rtb.Loaded -= Rtb_Loaded;
            rtb.Unloaded -= Rtb_Unloaded;
            rtb.DataContextChanged -= Rtb_DataContextChanged;
            rtb.PreviewKeyDown -= Rtb_PreviewKeyDown;
            rtb.TextChanged -= Rtb_TextChanged;
        }
    }

    private static void Rtb_Loaded(object sender, RoutedEventArgs e)
    {
        var rtb = (RichTextBox)sender;
        AppLogger.Debug("NoteEditor", $"RichTextBox Loaded. DataContext: {rtb.DataContext?.GetType().Name ?? "(null)"}");
        rtb.TextChanged -= Rtb_TextChanged;
        if (rtb.DataContext is NoteBlock block)
            LoadContent(rtb, block);
        else
        {
            rtb.Document.Blocks.Clear();
            SetIsEmpty(rtb, true);
        }
        rtb.TextChanged += Rtb_TextChanged;
    }

    private static void Rtb_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var rtb = (RichTextBox)sender;
        var oldId = (e.OldValue as NoteBlock)?.Id.ToString() ?? "(null)";
        var newId = (e.NewValue as NoteBlock)?.Id.ToString() ?? "(null)";
        AppLogger.Debug("NoteEditor", $"RichTextBox DataContextChanged: from {oldId} -> {newId}");

        rtb.TextChanged -= Rtb_TextChanged;
        if (e.NewValue is NoteBlock block)
            LoadContent(rtb, block);
        else
        {
            rtb.Document.Blocks.Clear();
            SetIsEmpty(rtb, true);
        }
        rtb.TextChanged += Rtb_TextChanged;
    }

    private static void Rtb_Unloaded(object sender, RoutedEventArgs e)
    {
        var rtb = (RichTextBox)sender;
        AppLogger.Debug("NoteEditor", "RichTextBox Unloaded");
        rtb.TextChanged -= Rtb_TextChanged;
    }

    private static void Rtb_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;

        if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            var caret = rtb.CaretPosition;
            if (caret is null) return;
            var currentPara = caret.Paragraph;
            if (currentPara is null) return;

            var checkboxContainer = currentPara.Inlines.OfType<InlineUIContainer>()
                .FirstOrDefault(c => c.Child is CheckBox);

            if (checkboxContainer is not null)
            {
                var textRange = new TextRange(checkboxContainer.ElementEnd, currentPara.ContentEnd);
                var text = textRange.Text.Trim();

                if (string.IsNullOrEmpty(text))
                {
                    // Empty checklist item -> user pressed Enter to exit checklist mode
                    currentPara.Inlines.Remove(checkboxContainer);
                    rtb.CaretPosition = currentPara.ContentStart;
                    e.Handled = true;
                    SaveContentToBlock(rtb);
                    return;
                }

                // Non-empty checklist item -> create next checklist item
                var nextPara = new Paragraph { Margin = currentPara.Margin };
                var nextBox = CreateInlineCheckBox(rtb);
                var nextContainer = new InlineUIContainer(nextBox);
                nextPara.Inlines.Add(nextContainer);

                // If caret is before the end of the text, move text after caret to the new paragraph
                var textAfterCaret = new TextRange(caret, currentPara.ContentEnd);
                var remaining = textAfterCaret.Text.TrimEnd('\r', '\n');
                if (!string.IsNullOrEmpty(remaining))
                {
                    textAfterCaret.Text = "";
                    nextPara.Inlines.Add(new Run(remaining));
                }

                if (currentPara.Parent is FlowDocument doc)
                {
                    doc.Blocks.InsertAfter(currentPara, nextPara);
                }
                else
                {
                    rtb.Document.Blocks.InsertAfter(currentPara, nextPara);
                }

                rtb.CaretPosition = nextContainer.ElementEnd;
                e.Handled = true;
                SaveContentToBlock(rtb);
                return;
            }
        }
        else if (e.Key == System.Windows.Input.Key.Back && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            var caret = rtb.CaretPosition;
            if (caret is null) return;
            var currentPara = caret.Paragraph;
            if (currentPara is null) return;

            var checkboxContainer = currentPara.Inlines.OfType<InlineUIContainer>()
                .FirstOrDefault(c => c.Child is CheckBox);

            if (checkboxContainer is not null)
            {
                var textBeforeCaret = new TextRange(checkboxContainer.ElementEnd, caret).Text;
                // If caret is right at the checkbox or the paragraph has no text
                if (string.IsNullOrEmpty(textBeforeCaret))
                {
                    currentPara.Inlines.Remove(checkboxContainer);
                    rtb.CaretPosition = currentPara.ContentStart;
                    e.Handled = true;
                    SaveContentToBlock(rtb);
                    return;
                }
            }
        }
    }

    private static void Rtb_TextChanged(object sender, TextChangedEventArgs e)
    {
        var rtb = (RichTextBox)sender;
        SaveContentToBlock(rtb);
    }

    public static void SaveContentToBlock(RichTextBox rtb)
    {
        try
        {
            var plainText = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n');
            bool hasMedia = HasMedia(rtb.Document);
            SetIsEmpty(rtb, plainText.Length == 0 && !hasMedia);

            if (rtb.DataContext is not NoteBlock block)
            {
                AppLogger.Warn("NoteEditor", "RichTextBox DataContext is not a NoteBlock - skipping save");
                return;
            }

            block.Text = plainText;
            block.Rtf = SaveContent(rtb);
            AppLogger.Debug("NoteEditor", $"SaveContentToBlock: Saved block {block.Id} (Text: {plainText.Length} chars, Rtf: {block.Rtf.Length} chars, HasMedia: {hasMedia}, Blocks: {rtb.Document.Blocks.Count})");
        }
        catch (Exception ex)
        {
            AppLogger.Error("NoteEditor", "EXCEPTION in SaveContentToBlock", ex);
        }
    }

    private static bool HasMedia(FlowDocument doc) => doc.Blocks.Any(b =>
        b is BlockUIContainer || (b is Paragraph p && p.Inlines.Any(i => i is InlineUIContainer)));

    public static string GetInlineAttachmentDirectory()
    {
        var dir = MediaPathResolver.InlineImagesDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetAttachmentsDirectory()
    {
        var dir = MediaPathResolver.AttachmentsDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A NoteBlock's PhotoPath is whatever absolute path the machine that created it used - it's
    // never rewritten after Drive sync downloads the actual bytes onto a different machine (or a
    // browser, which has no local path at all and stores just the bare filename there instead).
    // Falls back to looking the file up by name in the same local folders Drive sync already
    // downloads into, so a photo/file added from Tasky Web (or a second desktop machine) resolves
    // correctly here once its bytes have synced down, without needing PhotoPath itself to be
    // machine-portable.
    public static string? ResolveLocalAttachmentPath(NoteBlock block)
    {
        if (!string.IsNullOrWhiteSpace(block.PhotoPath) && File.Exists(block.PhotoPath))
            return block.PhotoPath;

        var fileName = block.FileName;
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var attachmentsPath = Path.Combine(GetAttachmentsDirectory(), fileName);
        if (File.Exists(attachmentsPath)) return attachmentsPath;

        var inlinePath = Path.Combine(GetInlineAttachmentDirectory(), fileName);
        if (File.Exists(inlinePath)) return inlinePath;

        return null;
    }

    private static readonly Regex EmbeddedMediaPathRegex = new(
        @"(?<attr>UriSource|Tag)=""(?<path>[^""]*[\\/](?<dir>InlineImages|Attachments)[\\/](?<file>[^""\\/]+))""",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// A NoteBlock's saved XAML bakes in whatever absolute local path the machine that inserted an
    /// image/file was using at the time (BitmapImage.UriSource for an inline image,
    /// CreateFileCard's Tag for a file chip - e.g. "C:\Users\stephart\Documents\Tasky\InlineImages\
    /// {guid}.png" on the machine that added it). Drive sync downloads the actual bytes into THIS
    /// machine's own Attachments/InlineImages folder (MediaPathResolver), but nothing ever
    /// rewrites the XAML text itself to match - so a synced image parses fine on the machine that
    /// created it and throws a XamlParseException everywhere else the note is opened (WPF's
    /// BitmapImage eagerly opens UriSource the instant the XAML is parsed, since InsertInlineImage
    /// sets BitmapCacheOption.OnLoad). LoadContent has no partial-failure recovery once
    /// XamlReader.Parse throws, so that doesn't just fail to show one image - it drops the block's
    /// entire content (every other image, all the text) back to an empty editor.
    ///
    /// Same problem ResolveLocalAttachmentPath above already solves for the older single-PhotoPath
    /// field: resolve by filename in this device's own local folders instead of trusting whatever
    /// absolute path got baked in on another machine. This rewrites every UriSource/Tag found
    /// pointing under an InlineImages/Attachments folder - regardless of machine, username, or
    /// whether the file even lived under OneDrive there - to this device's real local path for
    /// that same filename, before the XAML is ever parsed.
    /// </summary>
    public static string RewriteMediaPathsForThisDevice(string xaml)
    {
        return EmbeddedMediaPathRegex.Replace(xaml, m =>
        {
            var localDir = string.Equals(m.Groups["dir"].Value, "InlineImages", StringComparison.OrdinalIgnoreCase)
                ? GetInlineAttachmentDirectory()
                : GetAttachmentsDirectory();
            var localPath = Path.Combine(localDir, m.Groups["file"].Value);
            return $"{m.Groups["attr"].Value}=\"{localPath}\"";
        });
    }

    public static string CopyFileToAttachments(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath)) return sourceFilePath;

        var dir = GetAttachmentsDirectory();
        var fullSource = Path.GetFullPath(sourceFilePath);
        if (fullSource.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            return fullSource;

        var fileName = Path.GetFileName(sourceFilePath);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var destPath = Path.Combine(dir, fileName);

        int counter = 1;
        while (File.Exists(destPath))
        {
            destPath = Path.Combine(dir, $"{baseName} ({counter++}){ext}");
        }

        try
        {
            File.Copy(sourceFilePath, destPath, overwrite: false);
            AppLogger.Info("NoteEditor", $"Copied attachment '{sourceFilePath}' -> '{destPath}'");
            return destPath;
        }
        catch (Exception ex)
        {
            AppLogger.Error("NoteEditor", $"Failed to copy attachment '{sourceFilePath}' to '{destPath}'", ex);
            return sourceFilePath;
        }
    }

    // Each inline image gets its own file (guid-named) so a block can hold more than one.
    private static string SaveBitmapToInlineAttachment(System.Windows.Media.ImageSource source)
    {
        if (source is not System.Windows.Media.Imaging.BitmapSource bitmap)
            return string.Empty;

        var dir = GetInlineAttachmentDirectory();
        var path = Path.Combine(dir, $"{Guid.NewGuid()}.png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        return path;
    }

    // Legacy fallback: old saves used TextRange.Save(XamlPackage), which silently dropped
    // BlockUIContainer images. For files saved before the XamlWriter migration, the single
    // surviving image path is restored here so old tasks don't lose their photo on open.
    private static void RestoreInlinePhotoIfNeeded(RichTextBox rtb, NoteBlock block)
    {
        if (string.IsNullOrWhiteSpace(block.PhotoPath) || !File.Exists(block.PhotoPath)) return;

        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(block.PhotoPath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Margin = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var image = new Image
        {
            Source = bitmap,
            MaxWidth = 540,
            Stretch = System.Windows.Media.Stretch.Uniform
        };

        SetupImageInteractive(rtb, image, border);
        border.Child = image;
        rtb.Document.Blocks.Add(new BlockUIContainer(border));
    }

    private static void LoadContent(RichTextBox rtb, NoteBlock block)
    {
        rtb.Document.Blocks.Clear();
        AppLogger.Debug("NoteEditor", $"LoadContent: Loading block {block.Id} (RtfLen: {block.Rtf?.Length ?? 0}, TextLen: {block.Text?.Length ?? 0})");

        if (!string.IsNullOrEmpty(block.Rtf))
        {
            var raw = block.Rtf;
            if (raw.StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
                    range.Load(stream, DataFormats.Rtf);
                    HookDocumentHyperlinks(rtb.Document);
                    HookDocumentCheckboxes(rtb.Document, rtb);
                    HookDocumentImages(rtb.Document, rtb);
                    HookDocumentFileChips(rtb.Document, rtb);
                    SetIsEmpty(rtb, new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0 && !HasMedia(rtb.Document));
                    AppLogger.Info("NoteEditor", $"LoadContent: Loaded {rtb.Document.Blocks.Count} blocks from RTF stream");
                    return;
                }
                catch (NotSupportedException ex)
                {
                    AppLogger.Warn("NoteEditor", $"RTF load failed - falling back: {ex.Message}");
                    App.LogException(ex);
                }
            }
            else if (raw.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                // Current format: full FlowDocument XAML from XamlWriter.Save, which natively
                // round-trips BlockUIContainer images, checkboxes and tables in place.
                try
                {
                    if (XamlReader.Parse(RewriteMediaPathsForThisDevice(raw)) is FlowDocument loaded)
                    {
                        rtb.Document = loaded;
                        HookDocumentHyperlinks(rtb.Document);
                        HookDocumentCheckboxes(rtb.Document, rtb);
                        HookDocumentImages(rtb.Document, rtb);
                        HookDocumentFileChips(rtb.Document, rtb);
                        SetIsEmpty(rtb, new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0 && !HasMedia(rtb.Document));
                        AppLogger.Info("NoteEditor", $"LoadContent: Loaded {rtb.Document.Blocks.Count} blocks from native XAML stream");
                        return;
                    }
                }
                catch (XamlParseException ex)
                {
                    AppLogger.Warn("NoteEditor", $"XAML parse failed - falling back: {ex.Message}");
                    App.LogException(ex);
                }
            }
            else
            {
                // Legacy format: base64 TextRange.Save(XamlPackage), which silently strips images.
                try
                {
                    var bytes = Convert.FromBase64String(raw);
                    var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
                    using var stream = new MemoryStream(bytes);
                    range.Load(stream, DataFormats.XamlPackage);
                    if (!HasMedia(rtb.Document))
                        RestoreInlinePhotoIfNeeded(rtb, block);
                    HookDocumentHyperlinks(rtb.Document);
                    HookDocumentCheckboxes(rtb.Document, rtb);
                    HookDocumentImages(rtb.Document, rtb);
                    HookDocumentFileChips(rtb.Document, rtb);
                    SetIsEmpty(rtb, new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0 && !HasMedia(rtb.Document));
                    AppLogger.Info("NoteEditor", $"LoadContent: Loaded {rtb.Document.Blocks.Count} blocks from legacy XamlPackage");
                    return;
                }
                catch (FormatException ex)
                {
                    AppLogger.Warn("NoteEditor", $"Legacy XamlPackage Base64 decode failed: {ex.Message}");
                    App.LogException(ex);
                }
            }
        }

        if (!string.IsNullOrEmpty(block.PhotoPath) && File.Exists(block.PhotoPath) && !HasMedia(rtb.Document))
            RestoreInlinePhotoIfNeeded(rtb, block);

        if (!string.IsNullOrEmpty(block.Text))
            rtb.Document.Blocks.Add(new Paragraph(new Run(block.Text)));

        SetIsEmpty(rtb, new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0 && !HasMedia(rtb.Document));
        AppLogger.Debug("NoteEditor", $"LoadContent: Final block count: {rtb.Document.Blocks.Count}");
    }

    private static string SaveContent(RichTextBox rtb)
    {
        // XamlWriter can't serialize the delegate tuples/context menus we attach for
        // interactivity, so strip them before saving and restore them right after.
        var checkBoxes = rtb.Document.Blocks.OfType<Paragraph>()
            .SelectMany(p => p.Inlines.OfType<InlineUIContainer>())
            .Select(c => c.Child as CheckBox)
            .Where(cb => cb is not null)
            .Cast<CheckBox>()
            .ToList();
        var savedTags = checkBoxes.Select(cb => cb.Tag).ToList();
        foreach (var cb in checkBoxes) cb.Tag = null;

        var buis = rtb.Document.Blocks.OfType<BlockUIContainer>().ToList();
        foreach (var bui in buis)
        {
            if (bui.Child is FrameworkElement elem)
            {
                elem.ContextMenu = null;
                elem.Cursor = null;
                if (elem is Grid g)
                {
                    foreach (var child in g.Children.OfType<FrameworkElement>())
                    {
                        child.ContextMenu = null;
                        child.Cursor = null;
                        if (child is Border b && b.Child is FrameworkElement innerElem)
                        {
                            innerElem.ContextMenu = null;
                            innerElem.Cursor = null;
                        }
                    }
                }
                else if (elem is Border b && b.Child is FrameworkElement innerElem)
                {
                    innerElem.ContextMenu = null;
                    innerElem.Cursor = null;
                }
            }
        }

        try
        {
            var result = XamlWriter.Save(rtb.Document);
            AppLogger.Debug("NoteEditor", $"SaveContent: Serialized {rtb.Document.Blocks.Count} blocks to {result.Length} chars XAML");
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("NoteEditor", "ERROR in SaveContent", ex);
            App.LogException(ex);
            return string.Empty;
        }
        finally
        {
            for (int i = 0; i < checkBoxes.Count; i++) checkBoxes[i].Tag = savedTags[i];
            HookDocumentImages(rtb.Document, rtb);
            HookDocumentFileChips(rtb.Document, rtb);
        }
    }

    public static void HookDocumentHyperlinks(FlowDocument doc)
    {
        var blocks = doc.Blocks.ToList();
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                var inlines = p.Inlines.ToList();
                foreach (var inline in inlines)
                {
                    if (inline is Hyperlink link)
                    {
                        HookHyperlink(link);
                    }
                    else if (inline is Span s)
                    {
                        var spanInlines = s.Inlines.ToList();
                        foreach (var spanInline in spanInlines)
                        {
                            if (spanInline is Hyperlink subLink)
                            {
                                HookHyperlink(subLink);
                            }
                        }
                    }
                }
            }
        }
    }

    private static void HookHyperlink(Hyperlink link)
    {
        // None of the Insert*Hyperlink call sites set Cursor or Foreground on the Hyperlink they
        // create, so it was falling back to the note editor's plain-text color and the default
        // I-beam cursor - nothing distinguished it as a link at all besides the tooltip. Applying
        // both here, in the one place every hyperlink already passes through (insert time via
        // InsertInlineHyperlink/InsertHtmlClipboardContent/BuildImagePlaceholder, and load time
        // via HookDocumentHyperlinks), fixes it everywhere at once - including links already
        // sitting in existing notes, the next time they're opened. SetResourceReference (not a
        // one-time Foreground assignment) mirrors XAML's DynamicResource, so a link re-colors
        // correctly if the user switches Light/Dark theme while it's on screen, same as every
        // other themed brush in the app.
        link.Cursor = Cursors.Hand;
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");

        link.RequestNavigate -= Link_RequestNavigate;
        link.RequestNavigate += Link_RequestNavigate;

        link.PreviewMouseLeftButtonDown -= Link_PreviewMouseLeftButtonDown;
        link.PreviewMouseLeftButtonDown += Link_PreviewMouseLeftButtonDown;
    }

    public static void HookDocumentCheckboxes(FlowDocument doc, RichTextBox rtb)
    {
        var blocks = doc.Blocks.ToList();
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                var inlines = p.Inlines.ToList();
                foreach (var inline in inlines)
                {
                    if (inline is InlineUIContainer { Child: CheckBox cb })
                    {
                        // Remove any old handlers first to prevent accumulation
                        // Cast from object to tuple type to properly pattern match
                        if (cb.Tag is ValueTuple<RoutedEventHandler, RoutedEventHandler> oldHandlers)
                        {
                            cb.Checked -= oldHandlers.Item1;
                            cb.Unchecked -= oldHandlers.Item2;
                        }
                        
                        // Create handler references (not lambdas to avoid creating new instances)
                        RoutedEventHandler checkedHandler = (s, e) => SaveContentToBlock(rtb);
                        RoutedEventHandler uncheckedHandler = (s, e) => SaveContentToBlock(rtb);
                        
                        // Store for cleanup on next load
                        cb.Tag = (checkedHandler, uncheckedHandler);
                        
                        // Subscribe properly
                        cb.Checked += checkedHandler;
                        cb.Unchecked += uncheckedHandler;
                    }
                }
            }
        }
    }

    public static void HookDocumentImages(FlowDocument doc, RichTextBox rtb)
    {
        var blocks = doc.Blocks.ToList();
        foreach (var block in blocks)
        {
            if (block is BlockUIContainer bui)
            {
                if (bui.Child is Grid grid && (grid.Tag as string == "ImageContainer" || grid.Children.OfType<Border>().Any(b => b.Child is Image)))
                {
                    var img = grid.Children.OfType<Border>().Select(b => b.Child as Image).FirstOrDefault(i => i is not null);
                    if (img is not null)
                    {
                        SetupImageInteractive(rtb, img, grid);
                    }
                }
                else if (bui.Child is Border border && border.Child is Image img)
                {
                    border.Child = null;
                    var newContainer = CreateImageContainer(img, img.MaxWidth > 0 ? img.MaxWidth : 540);
                    SetupImageInteractive(rtb, img, newContainer);
                    bui.Child = newContainer;
                }
            }
        }
    }

    public static void HookDocumentFileChips(FlowDocument doc, RichTextBox rtb)
    {
        var blocks = doc.Blocks.ToList();
        foreach (var block in blocks)
        {
            if (block is BlockUIContainer bui)
            {
                if (bui.Child is Grid grid && grid.Tag is string filePath && !string.IsNullOrEmpty(filePath))
                {
                    SetupFileCardInteractive(rtb, grid, filePath);
                }
                else if (bui.Child is Border card && card.Tag is string legacyPath && card.Child is not Image)
                {
                    var newCard = CreateFileCard(legacyPath);
                    SetupFileCardInteractive(rtb, newCard, legacyPath);
                    bui.Child = newCard;
                }
                else if (bui.Child is Button chip && chip.Tag is string oldPath)
                {
                    var newCard = CreateFileCard(oldPath);
                    SetupFileCardInteractive(rtb, newCard, oldPath);
                    bui.Child = newCard;
                }
            }
        }
    }

    public static FrameworkElement CreateFileCard(string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        var outerGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 12, 4),
            Tag = filePath,
            Focusable = false,
            IsHitTestVisible = true,
            ForceCursor = true,
            Cursor = Cursors.Arrow
        };

        // Main card body with room on top/right for the floating badge
        var cardBody = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(14, 10, 20, 10),
            Margin = new Thickness(0, 8, 8, 0),
            Cursor = Cursors.Hand,
            ForceCursor = true,
            Focusable = false,
            IsHitTestVisible = true,
            Tag = "CardBody"
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };

        var iconBlock = new TextBlock
        {
            Text = GetFileIcon(filePath),
            FontSize = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameBlock = new TextBlock
        {
            Text = fileName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        };
        var sizeBlock = new TextBlock
        {
            Text = $"{GetFileSizeString(filePath)} • Double-click to open",
            FontSize = 11,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0)
        };
        textStack.Children.Add(nameBlock);
        textStack.Children.Add(sizeBlock);

        stack.Children.Add(iconBlock);
        stack.Children.Add(textStack);
        cardBody.Child = stack;

        // Floating delete button outside at top-right
        var delBorder = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(45, 52, 62)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(85, 95, 110)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ForceCursor = true,
            Tag = "DeleteAttachmentBtn",
            ToolTip = "Delete Attachment",
            Focusable = false,
            IsHitTestVisible = true
        };

        var delText = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 215, 225)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        delBorder.Child = delText;

        outerGrid.Children.Add(cardBody);
        outerGrid.Children.Add(delBorder);

        return outerGrid;
    }

    public static void SetupFileCardInteractive(RichTextBox rtb, FrameworkElement cardElement, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        cardElement.Tag = filePath;

        var cardBody = cardElement is Grid g
            ? g.Children.OfType<Border>().FirstOrDefault(b => b.Tag as string == "CardBody") ?? cardElement as Border
            : cardElement as Border;

        if (cardBody is not null)
        {
            cardBody.Cursor = Cursors.Hand;
            cardBody.Focusable = false;
            cardBody.IsHitTestVisible = true;
            cardBody.ToolTip = $"{fileName} ({GetFileSizeString(filePath)})\nDouble-click to open in default app\nRight-click for options";

            cardBody.MouseEnter -= Card_MouseEnter;
            cardBody.MouseEnter += Card_MouseEnter;
            cardBody.MouseLeave -= Card_MouseLeave;
            cardBody.MouseLeave += Card_MouseLeave;

            cardBody.PreviewMouseLeftButtonDown -= Card_PreviewMouseLeftButtonDown;
            cardBody.PreviewMouseLeftButtonDown += Card_PreviewMouseLeftButtonDown;

            cardBody.MouseLeftButtonDown -= Card_MouseLeftButtonDown;
            cardBody.MouseLeftButtonDown += Card_MouseLeftButtonDown;

            var cm = new ContextMenu();
            var openItem = new MenuItem { Header = "Open File" };
            openItem.Click += (s, e) => OpenFileTarget(filePath);

            var openFolderItem = new MenuItem { Header = "Open Containing Folder" };
            openFolderItem.Click += (s, e) =>
            {
                try
                {
                    if (File.Exists(filePath))
                        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                    else if (Directory.Exists(filePath))
                        Process.Start("explorer.exe", filePath);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("NoteEditor", "Failed to open containing folder", ex);
                }
            };

            var copyPathItem = new MenuItem { Header = "Copy File Path" };
            copyPathItem.Click += (s, e) =>
            {
                try { Clipboard.SetText(filePath); } catch { }
            };

            var saveAsItem = new MenuItem { Header = "Save Copy As..." };
            saveAsItem.Click += (s, e) =>
            {
                try
                {
                    var sfd = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = Path.GetFileName(filePath),
                        Filter = "All Files (*.*)|*.*"
                    };
                    if (sfd.ShowDialog() == true)
                        File.Copy(filePath, sfd.FileName, overwrite: true);
                }
                catch (Exception ex)
                {
                    ThemedMessageBox.Show($"Could not save copy: {ex.Message}", "Save Copy", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            var deleteItem = new MenuItem
            {
                Header = "Delete Attachment",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 60, 60))
            };
            deleteItem.Click += (s, e) =>
            {
                var parentBlock = rtb.Document.Blocks.OfType<BlockUIContainer>().FirstOrDefault(b => b.Child == cardElement);
                if (parentBlock is not null)
                {
                    rtb.Document.Blocks.Remove(parentBlock);
                    SaveContentToBlock(rtb);
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            AppLogger.Info("NoteEditor", $"Deleted attachment file '{filePath}'");
                        }
                    }
                    catch { }
                }
            };

            cm.Items.Add(openItem);
            cm.Items.Add(openFolderItem);
            cm.Items.Add(copyPathItem);
            cm.Items.Add(saveAsItem);
            cm.Items.Add(new Separator());
            cm.Items.Add(deleteItem);

            cardBody.ContextMenu = cm;
        }

        // Setup Floating ✕ Delete Button
        if (cardElement is Grid grid)
        {
            var delBtn = grid.Children.OfType<Border>().FirstOrDefault(b => b.Tag as string == "DeleteAttachmentBtn");
            if (delBtn is not null)
            {
                delBtn.MouseEnter -= DelBtn_MouseEnter;
                delBtn.MouseEnter += DelBtn_MouseEnter;
                delBtn.MouseLeave -= DelBtn_MouseLeave;
                delBtn.MouseLeave += DelBtn_MouseLeave;

                delBtn.PreviewMouseLeftButtonDown -= (s, e) => { };
                delBtn.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    var parentBlock = rtb.Document.Blocks.OfType<BlockUIContainer>().FirstOrDefault(b => b.Child == cardElement);
                    if (parentBlock is not null)
                    {
                        rtb.Document.Blocks.Remove(parentBlock);
                        SaveContentToBlock(rtb);
                        try
                        {
                            if (File.Exists(filePath))
                            {
                                File.Delete(filePath);
                                AppLogger.Info("NoteEditor", $"Deleted attachment '{filePath}' via ✕ button");
                            }
                        }
                        catch { }
                    }
                };
            }
        }

        cardElement.MouseEnter -= Container_MouseEnter;
        cardElement.MouseEnter += Container_MouseEnter;
        cardElement.MouseLeave -= Container_MouseLeave;
        cardElement.MouseLeave += Container_MouseLeave;
        cardElement.Unloaded -= Container_Unloaded;
        cardElement.Unloaded += Container_Unloaded;
    }

    private static void DelBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Color.FromRgb(214, 54, 56));
    }

    private static void DelBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Color.FromRgb(45, 52, 62));
    }

    public static FrameworkElement CreateImageContainer(Image image, double maxWidth = 540)
    {
        if (image.Parent is Decorator oldDecorator)
            oldDecorator.Child = null;
        else if (image.Parent is Panel oldPanel)
            oldPanel.Children.Remove(image);

        var outerGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 12, 4),
            Tag = "ImageContainer",
            Focusable = false,
            IsHitTestVisible = true,
            ForceCursor = true,
            Cursor = Cursors.Arrow
        };

        var imageBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
            Margin = new Thickness(0, 8, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ForceCursor = true,
            Tag = "ImageBorder"
        };
        image.MaxWidth = maxWidth;
        image.Stretch = Stretch.Uniform;
        image.Cursor = Cursors.Hand;
        image.ForceCursor = true;
        image.ToolTip = "Click to expand photo\nRight-click for resize/delete options";
        imageBorder.Child = image;

        // Border.ClipToBounds only clips to the element's rectangular bounds - it ignores
        // CornerRadius entirely, so the image's square corners would otherwise poke out past
        // the rounded outline. A geometry clip is the only way to actually round the content,
        // and it needs concrete pixel dimensions, so it's set from SizeChanged rather than once.
        imageBorder.SizeChanged += (s, e) =>
            imageBorder.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 6, 6);

        var delBorder = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(220, 35, 40, 50)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 110, 125)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ForceCursor = true,
            Tag = "DeleteImageBtn",
            ToolTip = "Delete Photo",
            Focusable = false,
            IsHitTestVisible = true
        };

        var delText = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        delBorder.Child = delText;

        outerGrid.Children.Add(imageBorder);
        outerGrid.Children.Add(delBorder);

        return outerGrid;
    }

    public static void SetupImageInteractive(RichTextBox rtb, Image image, FrameworkElement container)
    {
        image.Cursor = Cursors.Hand;
        image.ToolTip = "Click to expand photo\nRight-click for options";

        image.MouseLeftButtonDown -= Image_MouseLeftButtonDown;
        image.MouseLeftButtonDown += Image_MouseLeftButtonDown;

        var cm = new ContextMenu();
        var sizeSmall = new MenuItem { Header = "Size: Small (250px)" };
        sizeSmall.Click += (s, e) => { image.MaxWidth = 250; image.Width = 250; SaveContentToBlock(rtb); };
        var sizeMed = new MenuItem { Header = "Size: Medium (450px)" };
        sizeMed.Click += (s, e) => { image.MaxWidth = 450; image.Width = 450; SaveContentToBlock(rtb); };
        var sizeLarge = new MenuItem { Header = "Size: Large (650px)" };
        sizeLarge.Click += (s, e) => { image.MaxWidth = 650; image.Width = 650; SaveContentToBlock(rtb); };
        var sizeFull = new MenuItem { Header = "Size: Fit Width" };
        sizeFull.Click += (s, e) => { image.MaxWidth = double.PositiveInfinity; image.Width = double.NaN; SaveContentToBlock(rtb); };

        var viewFull = new MenuItem { Header = "View Fullscreen (Double-Click)" };
        viewFull.Click += (s, e) =>
        {
            if (image.Source is BitmapSource bmp)
            {
                var viewer = new PhotoViewerWindow(bmp) { Owner = Application.Current.MainWindow };
                viewer.Show();
            }
        };

        var copyImageItem = new MenuItem { Header = "Copy Image" };
        copyImageItem.Click += (s, e) =>
        {
            try
            {
                if (image.Source is BitmapSource bmp)
                    Clipboard.SetImage(bmp);
            }
            catch (Exception ex)
            {
                AppLogger.Error("NoteEditor", "Failed to copy image", ex);
            }
        };

        var saveAsItem = new MenuItem { Header = "Save Image As..." };
        saveAsItem.Click += (s, e) =>
        {
            try
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "photo.png",
                    Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|All Files (*.*)|*.*"
                };
                if (sfd.ShowDialog() == true && image.Source is BitmapSource bmp)
                {
                    using var stream = new FileStream(sfd.FileName, FileMode.Create);
                    var encoder = Path.GetExtension(sfd.FileName).ToLowerInvariant() switch
                    {
                        ".jpg" or ".jpeg" => (BitmapEncoder)new JpegBitmapEncoder(),
                        _ => new PngBitmapEncoder()
                    };
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    encoder.Save(stream);
                }
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"Could not save image: {ex.Message}", "Save Image", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        var deleteItem = new MenuItem
        {
            Header = "Delete Photo",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 60, 60))
        };
        deleteItem.Click += (s, e) =>
        {
            var parentBlock = rtb.Document.Blocks.OfType<BlockUIContainer>().FirstOrDefault(b => b.Child == container);
            if (parentBlock is not null)
            {
                rtb.Document.Blocks.Remove(parentBlock);
                SaveContentToBlock(rtb);
                try
                {
                    if (image.Source is BitmapImage bi && bi.UriSource is { IsAbsoluteUri: true, Scheme: "file" } uri)
                    {
                        var localFile = uri.LocalPath;
                        if (File.Exists(localFile))
                        {
                            File.Delete(localFile);
                            AppLogger.Info("NoteEditor", $"Deleted inline image file from disk: '{localFile}'");
                        }
                    }
                }
                catch { }
            }
        };

        cm.Items.Add(viewFull);
        cm.Items.Add(copyImageItem);
        cm.Items.Add(saveAsItem);
        cm.Items.Add(new Separator());
        cm.Items.Add(sizeSmall);
        cm.Items.Add(sizeMed);
        cm.Items.Add(sizeLarge);
        cm.Items.Add(sizeFull);
        cm.Items.Add(new Separator());
        cm.Items.Add(deleteItem);

        image.ContextMenu = cm;

        // Setup Floating ✕ Delete Button
        if (container is Grid grid)
        {
            var delBtn = grid.Children.OfType<Border>().FirstOrDefault(b => b.Tag as string == "DeleteImageBtn");
            if (delBtn is not null)
            {
                delBtn.MouseEnter -= DelBtn_MouseEnter;
                delBtn.MouseEnter += DelBtn_MouseEnter;
                delBtn.MouseLeave -= DelBtn_MouseLeave;
                delBtn.MouseLeave += DelBtn_MouseLeave;

                delBtn.PreviewMouseLeftButtonDown -= (s, e) => { };
                delBtn.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    var parentBlock = rtb.Document.Blocks.OfType<BlockUIContainer>().FirstOrDefault(b => b.Child == container);
                    if (parentBlock is not null)
                    {
                        rtb.Document.Blocks.Remove(parentBlock);
                        SaveContentToBlock(rtb);
                        try
                        {
                            if (image.Source is BitmapImage bi && bi.UriSource is { IsAbsoluteUri: true, Scheme: "file" } uri)
                            {
                                if (File.Exists(uri.LocalPath))
                                {
                                    File.Delete(uri.LocalPath);
                                    AppLogger.Info("NoteEditor", $"Deleted inline image file '{uri.LocalPath}' via ✕ button");
                                }
                            }
                        }
                        catch { }
                    }
                };
            }
        }

        container.MouseEnter -= Container_MouseEnter;
        container.MouseEnter += Container_MouseEnter;
        container.MouseLeave -= Container_MouseLeave;
        container.MouseLeave += Container_MouseLeave;
        container.Unloaded -= Container_Unloaded;
        container.Unloaded += Container_Unloaded;
    }

    private static void Container_MouseEnter(object sender, MouseEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Hand;
    }

    private static void Container_MouseLeave(object sender, MouseEventArgs e)
    {
        Mouse.OverrideCursor = null;
    }

    private static void Container_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Mouse.OverrideCursor == Cursors.Hand)
            Mouse.OverrideCursor = null;
    }

    private static void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border card)
            card.Background = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
    }

    private static void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border card)
            card.Background = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128));
    }

    private static void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is Border card)
        {
            var targetPath = card.Tag as string ?? (card.Parent as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrEmpty(targetPath))
            {
                AppLogger.Info("NoteEditor", $"Card double-click opening '{targetPath}'");
                OpenFileTarget(targetPath);
            }
        }
        e.Handled = true;
    }

    private static void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is Border card)
        {
            var targetPath = card.Tag as string ?? (card.Parent as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrEmpty(targetPath))
            {
                AppLogger.Info("NoteEditor", $"Card double-click opening '{targetPath}'");
                OpenFileTarget(targetPath);
            }
        }
        e.Handled = true;
    }

    private static void Card_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border card && card.ContextMenu is not null)
        {
            card.ContextMenu.PlacementTarget = card;
            card.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    public static void SetupFileChipInteractive(RichTextBox rtb, Button chip, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        chip.Cursor = Cursors.Hand;
        chip.Tag = filePath;
        chip.ToolTip = $"Click to open {fileName}\nRight-click for options";

        chip.Click -= FileChip_Click;
        chip.Click += FileChip_Click;
        chip.PreviewMouseLeftButtonDown -= FileChip_PreviewMouseLeftButtonDown;
        chip.PreviewMouseLeftButtonDown += FileChip_PreviewMouseLeftButtonDown;
    }

    private static void FileChip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button chip && chip.Tag is string filePath)
        {
            OpenFileTarget(filePath);
            e.Handled = true;
        }
    }

    private static void FileChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button chip && chip.Tag is string filePath)
        {
            OpenFileTarget(filePath);
            e.Handled = true;
        }
    }

    private static string GetFileIcon(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "📕",
            ".doc" or ".docx" => "📘",
            ".xls" or ".xlsx" or ".csv" => "📗",
            ".ppt" or ".pptx" => "📙",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "📦",
            ".txt" or ".log" or ".md" => "📄",
            ".mp3" or ".wav" or ".m4a" or ".flac" => "🎵",
            ".mp4" or ".mkv" or ".avi" or ".mov" => "🎬",
            ".exe" or ".msi" => "⚙️",
            ".cs" or ".js" or ".ts" or ".html" or ".css" or ".py" or ".json" or ".xml" => "💻",
            _ => "📎"
        };
    }

    private static string GetFileSizeString(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var len = new FileInfo(path).Length;
                if (len < 1024) return $"{len} B";
                if (len < 1024 * 1024) return $"{len / 1024.0:F1} KB";
                return $"{len / (1024.0 * 1024.0):F1} MB";
            }
        }
        catch { }
        return "File";
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com", ".pif", ".hta"
    };

    public static void OpenFileTarget(string filePath)
    {
        try
        {
            AppLogger.Info("NoteEditor", $"Opening attached file: '{filePath}'");
            if (!File.Exists(filePath) && !Directory.Exists(filePath))
            {
                ThemedMessageBox.Show($"The file could not be found at:\n{filePath}", "Open File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // filePath is loaded straight from the .tasky JSON (embedded in the block's RTF).
            // Refusing anything outside the Attachments folder (the only place
            // CopyFileToAttachments ever writes to) means a hand-edited or restored-from-an-
            // untrusted-backup file can't point this at an arbitrary local file and have it
            // opened with no warning.
            if (!Path.GetFullPath(filePath).StartsWith(GetAttachmentsDirectory() + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("NoteEditor", $"Refused to open file outside Attachments folder: '{filePath}'");
                ThemedMessageBox.Show($"This file is outside Tasky's Attachments folder and can't be opened from here:\n{filePath}", "Open File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ExecutableExtensions.Contains(Path.GetExtension(filePath)))
            {
                var confirm = ThemedMessageBox.Show(
                    $"\"{Path.GetFileName(filePath)}\" is an executable program or script. Opening it will execute code on your computer.\n\nDo you want to continue?",
                    "Security Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            var psi = new ProcessStartInfo(filePath) { UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLogger.Error("NoteEditor", $"Failed to open attached file '{filePath}'", ex);
            ThemedMessageBox.Show($"Unable to open file:\n{ex.Message}", "Open File", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void Image_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Image img && img.Source is BitmapSource bmp)
        {
            var viewer = new PhotoViewerWindow(bmp) { Owner = Application.Current.MainWindow };
            viewer.Show();
            e.Handled = true;
        }
    }

    private static void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenHyperlink(e.Uri);
        e.Handled = true;
    }

    // WPF only raises Hyperlink.RequestNavigate for a Hyperlink hosted inside an EDITABLE
    // RichTextBox when Ctrl is held during the click - a plain click positions the text caret
    // instead, since editing takes priority by default. Every Insert*Hyperlink call site's
    // tooltip promises "(Click to open)", not "(Ctrl+Click to open)", so that default silently
    // breaks the UI's own promise: pasting/inserting a link produces something that looks
    // clickable but does nothing on a normal click. PreviewMouseLeftButtonDown is a tunneling
    // event WPF raises regardless of modifier keys, so hooking it directly on the Hyperlink makes
    // a plain click open the link too, without changing normal caret placement/selection
    // anywhere else in the document.
    private static void Link_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Hyperlink { NavigateUri: { } uri })
        {
            OpenHyperlink(uri);
            e.Handled = true;
        }
    }

    private static void OpenHyperlink(Uri uri)
    {
        try
        {
            // uri ultimately comes from the .tasky JSON (embedded in the block's RTF).
            // Restricting to http(s) means a hyperlink whose target was ever hand-edited or
            // restored from an untrusted backup (e.g. a file:// URI pointing at a local
            // executable) can't get handed to ShellExecute and launched with no warning.
            if (uri.Scheme is not ("http" or "https"))
            {
                AppLogger.Warn("NoteEditor", $"Refused to open link with non-http(s) scheme: '{uri}'");
                ThemedMessageBox.Show($"This link's address isn't a web link and can't be opened from here:\n{uri}", "Open Link", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Couldn't start the process (URL handler not found, etc.)
            App.LogException(ex);
        }
        catch (System.InvalidOperationException ex)
        {
            // Process.Start threw an error
            App.LogException(ex);
        }
    }

    public static CheckBox CreateInlineCheckBox(RichTextBox rtb, bool isChecked = false)
    {
        var checkBox = new CheckBox
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        checkBox.Checked += (s, e) => SaveContentToBlock(rtb);
        checkBox.Unchecked += (s, e) => SaveContentToBlock(rtb);
        return checkBox;
    }

    private static NoteBlock? GetBoundBlock(RichTextBox rtb)
    {
        if (rtb.DataContext is NoteBlock dataBlock)
            return dataBlock;

        if (rtb.Tag is NoteBlock tagBlock)
            return tagBlock;

        return null;
    }

    public static void InsertInlineImage(RichTextBox rtb, System.Windows.Media.ImageSource bitmapSource, double maxWidth = 540)
    {
        var boundBlock = GetBoundBlock(rtb);
        AppLogger.Info("NoteEditor", $"InsertInlineImage: Inserting image into RichTextBox (Block ID: {boundBlock?.Id})");

        // Persist to disk with a guid filename (supports multiple images per block) and point
        // the inserted Image at that file so XamlWriter.Save can serialize Source as a file URI
        // instead of silently failing to round-trip an in-memory-only bitmap (e.g. clipboard paste).
        var attachmentPath = SaveBitmapToInlineAttachment(bitmapSource);
        var persistedSource = bitmapSource;
        if (!string.IsNullOrWhiteSpace(attachmentPath))
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(attachmentPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            persistedSource = bitmap;
            AppLogger.Debug("NoteEditor", $"InsertInlineImage: Persisted bitmap to '{attachmentPath}'");
        }

        if (boundBlock is not null)
            rtb.Tag = boundBlock;

        var image = new Image
        {
            Source = persistedSource,
            MaxWidth = maxWidth,
            Stretch = Stretch.Uniform
        };
        var containerGrid = CreateImageContainer(image, maxWidth);
        SetupImageInteractive(rtb, image, containerGrid);

        var container = new BlockUIContainer(containerGrid);

        var insertion = rtb.CaretPosition ?? rtb.Document.ContentEnd;
        var currentPara = insertion.Paragraph;
        if (currentPara is not null)
        {
            rtb.Document.Blocks.InsertAfter(currentPara, container);
            var nextPara = new Paragraph();
            rtb.Document.Blocks.InsertAfter(container, nextPara);
            rtb.CaretPosition = nextPara.ContentStart;
        }
        else
        {
            rtb.Document.Blocks.Add(container);
            var nextPara = new Paragraph();
            rtb.Document.Blocks.Add(nextPara);
            rtb.CaretPosition = nextPara.ContentStart;
        }
        rtb.Focus();
        SaveContentToBlock(rtb);
        AppLogger.Info("NoteEditor", $"InsertInlineImage: Successfully inserted and saved inline image (Doc blocks: {rtb.Document.Blocks.Count})");
    }

    public static void InsertInlineTable(RichTextBox rtb, int rows, int cols)
    {
        rows = Math.Clamp(rows, 1, 50);
        cols = Math.Clamp(cols, 1, 20);

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 222))
        };

        for (int c = 0; c < cols; c++)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(100.0 / cols, GridUnitType.Star) });
        }

        var rowGroup = new TableRowGroup();

        // Header row
        var headerRow = new TableRow
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(25, 128, 128, 128))
        };
        for (int c = 0; c < cols; c++)
        {
            var headerCell = new TableCell(new Paragraph(new Bold(new Run($"Header {c + 1}"))))
            {
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 222))
            };
            headerRow.Cells.Add(headerCell);
        }
        rowGroup.Rows.Add(headerRow);

        // Data rows
        for (int r = 1; r < rows; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                var cell = new TableCell(new Paragraph(new Run("")))
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 222))
                };
                row.Cells.Add(cell);
            }
            rowGroup.Rows.Add(row);
        }

        table.RowGroups.Add(rowGroup);

        var insertion = rtb.CaretPosition ?? rtb.Document.ContentEnd;
        var currentPara = insertion.Paragraph;
        if (currentPara is not null)
        {
            rtb.Document.Blocks.InsertAfter(currentPara, table);
            var nextPara = new Paragraph();
            rtb.Document.Blocks.InsertAfter(table, nextPara);
            rtb.CaretPosition = nextPara.ContentStart;
        }
        else
        {
            rtb.Document.Blocks.Add(table);
            var nextPara = new Paragraph();
            rtb.Document.Blocks.Add(nextPara);
            rtb.CaretPosition = nextPara.ContentStart;
        }

        rtb.Focus();
        SaveContentToBlock(rtb);
    }

    public static void InsertInlineHyperlink(RichTextBox rtb, string url, string? displayText = null)
    {
        displayText = string.IsNullOrWhiteSpace(displayText) ? url : displayText;
        var hyperlink = new Hyperlink(new Run(displayText))
        {
            NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : new Uri("https://" + url),
            ToolTip = $"{url} (Click to open)"
        };
        HookHyperlink(hyperlink);

        var caret = rtb.CaretPosition ?? rtb.Document.ContentEnd;
        var paragraph = caret.Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            rtb.Document.Blocks.Add(paragraph);
            caret = paragraph.ContentStart;
        }

        var span = new Span();
        span.Inlines.Add(hyperlink);
        span.Inlines.Add(new Run(" "));

        paragraph.Inlines.Add(span);
        rtb.CaretPosition = paragraph.ContentEnd;
        rtb.Focus();
    }

    // Public rather than private, same reasoning as GoogleDriveService's
    // GetReferencedAttachmentFilenames/ParseBlockReferences: this is the pure, non-UI part of
    // HTML-clipboard paste (regex/entity parsing) - exposed so TodoApp.Tests can cover the
    // actual bug-prone logic directly, without needing a live RichTextBox on a UI thread.
    public enum HtmlSegmentKind { Text, Link, Image }

    public sealed record HtmlSegment(HtmlSegmentKind Kind, string Text, string? Url);

    private static readonly Regex HtmlAnchorOrImageRegex = new(
        @"<a\b(?<attrs>[^>]*)>(?<inner>.*?)</a\s*>|<img\b(?<imgattrs>[^>]*?)/?>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlAttributeRegex = new(
        @"(?<name>[\w-]+)\s*=\s*(?:""(?<val1>[^""]*)""|'(?<val2>[^']*)'|(?<val3>[^\s>]+))",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Reconstructs a paste from the clipboard's HTML representation (DataFormats.Html) - the one
    /// format WPF's stock RichTextBox.Paste() completely ignores (it only understands
    /// Xaml/Rtf/plain text). That's exactly what apps like Slack/Teams put on the clipboard for a
    /// "copy message": Html plus a much poorer plain-text fallback, no Rtf, and (for an inline
    /// photo) no real image bytes - chat apps reference images by a CDN URL rather than embedding
    /// them, so there is nothing to actually paste as a bitmap. Without this, that plain-text
    /// fallback is all Tasky ever saw: a real hyperlink collapsed to its bare display text, and an
    /// image vanished with no indication anything was dropped - sometimes leaving stray fragments
    /// (a filename, a timestamp) jammed straight into the surrounding sentence with no spacing.
    ///
    /// This isn't a full HTML renderer - just enough structure (links, images, paragraph/line/
    /// table-row breaks) to keep a pasted chat message readable: a &lt;a href&gt; becomes a real,
    /// clickable inline hyperlink (the same shape InsertInlineHyperlink produces), and an
    /// unreachable &lt;img&gt; becomes a clearly-labeled placeholder run - linked back to its
    /// source URL when there is one - instead of raw jammed text.
    /// </summary>
    public static void InsertHtmlClipboardContent(RichTextBox rtb, string cfHtml)
    {
        var fragment = ExtractHtmlFragment(cfHtml);
        var segments = ParseHtmlSegments(fragment);
        if (segments.Count == 0) return;

        var caret = rtb.CaretPosition ?? rtb.Document.ContentEnd;
        var paragraph = caret.Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            rtb.Document.Blocks.Add(paragraph);
        }

        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case HtmlSegmentKind.Text:
                    paragraph = AppendTextWithLineBreaks(rtb, paragraph, segment.Text);
                    break;

                case HtmlSegmentKind.Link:
                    var hyperlink = new Hyperlink(new Run(segment.Text))
                    {
                        NavigateUri = Uri.TryCreate(segment.Url, UriKind.Absolute, out var linkUri) ? linkUri : new Uri("https://" + segment.Url),
                        ToolTip = $"{segment.Url} (Click to open)"
                    };
                    HookHyperlink(hyperlink);
                    paragraph.Inlines.Add(hyperlink);
                    break;

                case HtmlSegmentKind.Image:
                    paragraph.Inlines.Add(BuildImagePlaceholder(segment));
                    break;
            }
        }

        rtb.CaretPosition = paragraph.ContentEnd;
        rtb.Focus();
        SaveContentToBlock(rtb);
    }

    private static Paragraph AppendTextWithLineBreaks(RichTextBox rtb, Paragraph paragraph, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
                paragraph.Inlines.Add(new Run(lines[i]));

            if (i < lines.Length - 1)
            {
                var nextPara = new Paragraph();
                rtb.Document.Blocks.InsertAfter(paragraph, nextPara);
                paragraph = nextPara;
            }
        }
        return paragraph;
    }

    private static Inline BuildImagePlaceholder(HtmlSegment segment)
    {
        var label = string.IsNullOrWhiteSpace(segment.Text) ? "image" : segment.Text;
        var caption = $"[image: {label} — not pasted; copy the photo itself to include it]";

        if (Uri.TryCreate(segment.Url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var link = new Hyperlink(new Run(caption))
            {
                NavigateUri = uri,
                ToolTip = $"{segment.Url} (Click to open the original)",
                FontStyle = FontStyles.Italic
            };
            HookHyperlink(link);
            return link;
        }

        return new Run(caption) { FontStyle = FontStyles.Italic, Foreground = Brushes.Gray };
    }

    // CF_HTML wraps the actual markup in a small header (Version/StartHTML/EndHTML/
    // StartFragment/EndFragment) whose numeric offsets are byte offsets into the source app's own
    // encoding of the payload - not safe to use as .NET string indices without redoing that
    // encoding math. The <!--StartFragment--> / <!--EndFragment--> comment markers the spec also
    // requires are plain text landmarks in the same string Clipboard.GetData already handed back,
    // so anchoring on those instead sidesteps the encoding question entirely.
    public static string ExtractHtmlFragment(string cfHtml)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        var startIdx = cfHtml.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        var endIdx = cfHtml.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (startIdx >= 0 && endIdx > startIdx)
            return cfHtml[(startIdx + startMarker.Length)..endIdx];

        var bodyMatch = Regex.Match(cfHtml, @"<body[^>]*>(?<body>.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (bodyMatch.Success) return bodyMatch.Groups["body"].Value;

        var htmlIdx = cfHtml.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        return htmlIdx >= 0 ? cfHtml[htmlIdx..] : cfHtml;
    }

    private static string? ExtractAttribute(string attrsText, string name)
    {
        foreach (Match m in HtmlAttributeRegex.Matches(attrsText))
        {
            if (!string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase)) continue;
            var val = m.Groups["val1"].Success ? m.Groups["val1"].Value
                : m.Groups["val2"].Success ? m.Groups["val2"].Value
                : m.Groups["val3"].Value;
            return WebUtility.HtmlDecode(val);
        }
        return null;
    }

    private static string FileNameFromUrl(string url)
    {
        var noQuery = url.Split('?', '#')[0];
        var idx = noQuery.LastIndexOf('/');
        return idx >= 0 && idx < noQuery.Length - 1 ? noQuery[(idx + 1)..] : noQuery;
    }

    public static List<HtmlSegment> ParseHtmlSegments(string fragment)
    {
        fragment = Regex.Replace(fragment, @"<script\b.*?</script\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        fragment = Regex.Replace(fragment, @"<style\b.*?</style\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var segments = new List<HtmlSegment>();
        var lastIndex = 0;

        void AddTextGap(string gap)
        {
            var text = HtmlFragmentToPlainText(gap);
            if (text.Length > 0) segments.Add(new HtmlSegment(HtmlSegmentKind.Text, text, null));
        }

        foreach (Match match in HtmlAnchorOrImageRegex.Matches(fragment))
        {
            AddTextGap(fragment[lastIndex..match.Index]);

            if (match.Groups["imgattrs"].Success)
            {
                var src = ExtractAttribute(match.Groups["imgattrs"].Value, "src");
                var alt = ExtractAttribute(match.Groups["imgattrs"].Value, "alt");
                var label = !string.IsNullOrWhiteSpace(alt) ? alt
                    : !string.IsNullOrEmpty(src) ? FileNameFromUrl(src)
                    : "image";
                segments.Add(new HtmlSegment(HtmlSegmentKind.Image, label, src));
            }
            else
            {
                var href = ExtractAttribute(match.Groups["attrs"].Value, "href");
                var displayText = HtmlFragmentToPlainText(match.Groups["inner"].Value).Trim();
                if (!string.IsNullOrEmpty(href))
                    segments.Add(new HtmlSegment(HtmlSegmentKind.Link, string.IsNullOrEmpty(displayText) ? href : displayText, href));
                else if (displayText.Length > 0)
                    segments.Add(new HtmlSegment(HtmlSegmentKind.Text, displayText, null));
            }

            lastIndex = match.Index + match.Length;
        }

        AddTextGap(fragment[lastIndex..]);
        return segments;
    }

    public static string HtmlFragmentToPlainText(string html)
    {
        var text = Regex.Replace(html, @"</p\s*>|<br\s*/?>|</div\s*>|</li\s*>|</tr\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</td\s*>|</th\s*>", " | ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace(' ', ' ').Replace("\r\n", "\n").Replace('\r', '\n');
        // A row's last cell leaves a dangling " | " with nothing after it once </tr> becomes the
        // following newline - strip that before the general trailing-whitespace cleanup below, or
        // the lone "|" survives as a meaningless trailing character on the line.
        text = Regex.Replace(text, @"[ \t]*\|[ \t]*\n", "\n");
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim('\n', ' ');
    }

    public static void InsertInlineChecklist(RichTextBox rtb, string itemText = "")
    {
        var caret = rtb.CaretPosition ?? rtb.Document.ContentEnd;
        var paragraph = caret.Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            rtb.Document.Blocks.Add(paragraph);
            caret = paragraph.ContentStart;
        }

        var existingContainer = paragraph.Inlines.OfType<InlineUIContainer>()
            .FirstOrDefault(c => c.Child is CheckBox);

        if (existingContainer is null)
        {
            var checkBox = CreateInlineCheckBox(rtb);
            var container = new InlineUIContainer(checkBox);
            if (paragraph.Inlines.Count > 0)
                paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline, container);
            else
                paragraph.Inlines.Add(container);

            if (!string.IsNullOrEmpty(itemText))
                paragraph.Inlines.Add(new Run(itemText));

            rtb.CaretPosition = container.ElementEnd;
        }
        else
        {
            rtb.CaretPosition = existingContainer.ElementEnd;
        }

        rtb.Focus();
        SaveContentToBlock(rtb);
    }

    public static void InsertInlineFileChip(RichTextBox rtb, string filePath)
    {
        var localPath = CopyFileToAttachments(filePath);
        var card = CreateFileCard(localPath);
        SetupFileCardInteractive(rtb, card, localPath);

        var container = new BlockUIContainer(card);
        var caret = rtb.CaretPosition ?? rtb.Document.ContentEnd;
        var currentPara = caret.Paragraph;
        if (currentPara is not null)
        {
            rtb.Document.Blocks.InsertAfter(currentPara, container);
            var nextPara = new Paragraph();
            rtb.Document.Blocks.InsertAfter(container, nextPara);
            rtb.CaretPosition = nextPara.ContentStart;
        }
        else
        {
            rtb.Document.Blocks.Add(container);
            var nextPara = new Paragraph();
            rtb.Document.Blocks.Add(nextPara);
            rtb.CaretPosition = nextPara.ContentStart;
        }
        rtb.Focus();
        SaveContentToBlock(rtb);
        AppLogger.Info("NoteEditor", $"InsertInlineFileChip: Attached and saved file '{localPath}'");
    }
}
