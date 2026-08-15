using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TodoApp.Behaviors;
using TodoApp.Models;
using TodoApp.Services;
using TodoApp.ViewModels;

namespace TodoApp;

public partial class MainWindow : Window
{
    private const int HotkeyId = 9000;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly MainViewModel _viewModel;
    private RichTextBox? _activeEditor;
    private HwndSource? _hwndSource;
    private bool _readyToClose;
    private bool _flushInProgress;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _viewModel = new MainViewModel();
        }
        catch (Exception ex)
        {
            // The data file is inaccessible or corrupt - log and show error, then exit cleanly
            App.LogException(ex);
            ThemedMessageBox.Show(
                $"Tasky couldn't open your data file:\n\n{ex.Message}",
                "Couldn't Open Data File", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
            return;
        }

        DataContext = _viewModel;
        ApplySavedWindowState();
        ThemeService.ApplyTitleBar(this);

        Loaded += (_, _) =>
        {
            AppLogger.Info("MainWindow", "MainWindow Loaded event fired - bringing window to foreground");
            Activate();
            Focus();
        };

        _viewModel.FocusTitleRequested += () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                TitleTextBox.Focus();
                TitleTextBox.SelectAll();
            });
        };

        // The task list's extended multi-selection isn't bindable (SelectedItems isn't a DP), so
        // it doesn't get cleared just because SelectedTask/SelectedSidebarItem changes - do it
        // explicitly whenever the active section changes.
        //
        // NOTE: this used to also imperatively set SidebarListBox.SelectedItem / TagListBox.SelectedItem
        // here to keep the two sidebar lists mutually exclusive. That created a feedback loop -
        // explicitly setting one list's SelectedItem pushes a value back through its own TwoWay
        // binding to SelectedSidebarItem, which re-fires this handler, which sets the other list,
        // which pushes back again - an infinite cycle that crashed the app with a
        // StackOverflowException. Removed; the two lists sharing one bound property already leaves
        // at most one of them showing a real selection in practice, since a list only visually
        // selects an item that's actually present in its own ItemsSource.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedSidebarItem)) return;
            TaskListBox.SelectedItems.Clear();
        };

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsDarkTheme))
                ThemeService.ApplyTitleBar(this);
        };

        // The formatting toolbar only does anything while a text block's RichTextBox has focus -
        // switching tasks entirely (not just losing focus within the same task) is a backstop
        // reset in case the old RichTextBox left the visual tree without firing LostFocus.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedTaskDetail)) return;
            _activeEditor = null;
            FormattingGroup.IsEnabled = false;
            FormattingGroup.Visibility = Visibility.Collapsed;
            FormattingGroupSeparator.Visibility = Visibility.Collapsed;
        };

        _viewModel.Tray.NewTaskRequested += () => Dispatcher.Invoke(ShowQuickAdd);
        _viewModel.Tray.ShowRequested += () => Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
        _viewModel.Tray.ExitRequested += () => Dispatcher.Invoke(Close);

        // Autosave now writes off the UI thread (see MainViewModel.Save), so a plain synchronous
        // flush here could let the process exit before the last edit actually lands on disk.
        // Cancel the close and do the async work first - but crash logs from live testing showed
        // that calling THIS SAME window's Close() again afterward still throws
        // InvalidOperationException ("...while a Window is closing"), even on a single, non-
        // reentrant close request: apparently Window.Close() -> a canceled Closing -> Close()
        // again on the identical Window instance isn't reliably safe in this runtime - WPF still
        // considers it "closing" from the first attempt. Hiding the window and calling
        // Application.Current.Shutdown() instead sidesteps that entirely: Shutdown() closes each
        // open window itself as part of tearing down, so MainWindow's Closing fires once more from
        // a fresh, non-canceled request - _readyToClose just lets that final one through instead
        // of calling Close() a second time ourselves.
        Closing += async (_, e) =>
        {
            if (_readyToClose) return;
            e.Cancel = true;
            if (_flushInProgress) return;
            _flushInProgress = true;

            var wasMaximized = WindowState == WindowState.Maximized;
            var isMinimized = WindowState == WindowState.Minimized;
            var bounds = (wasMaximized || isMinimized) ? RestoreBounds : new Rect(Left, Top, Width, Height);
            Hide();

            // If the flush itself throws (e.g. a locked file mid-shutdown), everything below MUST
            // still run - otherwise _readyToClose never gets set, the window is already hidden,
            // and every future close attempt just re-cancels forever: an invisible, unkillable
            // process. Closing with a possibly-unsaved last edit beats that outcome.
            try
            {
                await _viewModel.FlushPendingSaveAsync();
            }
            catch (Exception ex)
            {
                App.LogException(ex);
            }

            // SaveWindowState/Shutdown get the same "must not prevent shutdown" treatment as the
            // flush above - SettingsStore.Save already swallows its own write failures, but
            // wrapping this too means the guarantee holds even if something in here changes later.
            try
            {
                if (bounds.Width > 100 && bounds.Height > 100 && !double.IsNaN(bounds.Left) && !double.IsNaN(bounds.Top) && bounds.Left > -10000 && bounds.Top > -10000)
                {
                    _viewModel.SaveWindowState(bounds.Left, bounds.Top, bounds.Width, bounds.Height, wasMaximized);
                }
                _viewModel.Shutdown();
            }
            catch (Exception ex)
            {
                App.LogException(ex);
            }

            _readyToClose = true;
            Application.Current.Shutdown();
        };
    }

    // Applies the saved window size/position before the window is shown. Only repositions if the
    // saved point still falls within the current virtual screen, so a monitor that's since been
    // unplugged can't strand the window off-screen.
    private void ApplySavedWindowState()
    {
        Width = Math.Max(860, _viewModel.SavedWindowWidth > 0 ? _viewModel.SavedWindowWidth : 1180);
        Height = Math.Max(520, _viewModel.SavedWindowHeight > 0 ? _viewModel.SavedWindowHeight : 740);

        if (_viewModel.SavedWindowLeft is { } left && _viewModel.SavedWindowTop is { } top &&
            !double.IsNaN(left) && !double.IsNaN(top) && left > -10000 && top > -10000 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100 &&
            left >= SystemParameters.VirtualScreenLeft &&
            top >= SystemParameters.VirtualScreenTop)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (_viewModel.SavedWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);
        RegisterHotKey(_hwndSource.Handle, HotkeyId, ModControl | ModAlt, (uint)KeyInterop.VirtualKeyFromKey(Key.T));
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource is not null)
        {
            UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            _hwndSource.RemoveHook(WndProc);
        }
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ShowQuickAdd();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // Global-hotkey / tray "New Task" entry point: a small always-on-top box that creates a task
    // without bringing the full window forward.
    private void ShowQuickAdd()
    {
        var quickAdd = new QuickAddWindow();
        if (quickAdd.ShowDialog() == true && !string.IsNullOrWhiteSpace(quickAdd.TaskTitle))
            _viewModel.AddQuickTask(quickAdd.TaskTitle!);
    }

    private void Body_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (_viewModel.SelectedTaskDetail is null)
        {
            AppLogger.Warn("MainWindow", "Body_Drop: Drop ignored - SelectedTaskDetail is null");
            return;
        }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        e.Handled = true;
        AppLogger.Info("MainWindow", $"Body_Drop: Received {paths.Length} dropped file(s)");

        var point = e.GetPosition(NoteEditor);
        var pos = NoteEditor.GetPositionFromPoint(point, snapToText: true);
        if (pos is not null) NoteEditor.CaretPosition = pos;

        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (TaskDetailViewModel.ImageExtensions.Contains(ext))
            {
                try
                {
                    AppLogger.Debug("MainWindow", $"Body_Drop: Loading dropped image '{path}'");
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    RichTextBoxBehavior.InsertInlineImage(NoteEditor, bmp);
                }
                catch (NotSupportedException ex)
                {
                    AppLogger.Error("MainWindow", $"Unsupported image format: {path}", ex);
                    App.LogException(ex);
                }
                catch (System.IO.FileNotFoundException ex)
                {
                    AppLogger.Error("MainWindow", $"Image file not found: {path}", ex);
                    App.LogException(ex);
                }
            }
            else
            {
                AppLogger.Debug("MainWindow", $"Body_Drop: Inserting file chip for '{path}'");
                RichTextBoxBehavior.InsertInlineFileChip(NoteEditor, path);
            }
        }
    }

    private void Body_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Body_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Body_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void AddPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTaskDetail is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert Image",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FileNames)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    RichTextBoxBehavior.InsertInlineImage(NoteEditor, bmp);
                }
                catch (NotSupportedException ex)
                {
                    App.LogException(ex);
                    ThemedMessageBox.Show($"Unsupported image format: {Path.GetFileName(path)}", "Image Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (System.IO.FileNotFoundException ex)
                {
                    App.LogException(ex);
                    ThemedMessageBox.Show($"Image file not found: {Path.GetFileName(path)}", "Image Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private void AddLink_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTaskDetail is null) return;
        var prompt = new LinkPromptWindow { Owner = this };
        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.UrlResult))
        {
            RichTextBoxBehavior.InsertInlineHyperlink(NoteEditor, prompt.UrlResult);
        }
    }

    private void AddChecklist_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTaskDetail is null) return;
        RichTextBoxBehavior.InsertInlineChecklist(NoteEditor);
    }

    private void AddTable_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTaskDetail is null) return;
        var dialog = new TablePromptWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RichTextBoxBehavior.InsertInlineTable(NoteEditor, dialog.Rows, dialog.Columns);
        }
    }

    private void ExportNote_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTask is not { } task) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Task Note",
            Filter = "HTML Document (*.html)|*.html|Markdown Document (*.md)|*.md|Print / PDF Document (*.*)|*.*",
            FileName = $"{task.Text}.html"
        };

        if (dialog.ShowDialog() == true)
        {
            var ext = Path.GetExtension(dialog.FileName)?.ToLowerInvariant() ?? string.Empty;
            if (ext == ".md")
            {
                ExportService.ExportToMarkdown(task, NoteEditor.Document, dialog.FileName);
                ThemedMessageBox.Show($"Task exported successfully to Markdown:\n{dialog.FileName}", "Export Note", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (ext == ".html" || ext == ".htm")
            {
                ExportService.ExportToHtml(task, NoteEditor.Document, dialog.FileName);
                ThemedMessageBox.Show($"Task exported successfully to HTML:\n{dialog.FileName}", "Export Note", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ExportService.PrintDocument(NoteEditor.Document, task.Text);
            }
        }
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTaskDetail is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert File",
            Filter = "All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                RichTextBoxBehavior.InsertInlineFileChip(NoteEditor, file);
            }
        }
    }

    // The checklist item list lives inside ChecklistBlockTemplate, so unlike BlockItemsControl
    // there's a fresh ItemsControl instance per checklist block, not one stable named element -
    // wire each one up as it loads. Tag doubles as an "already wired" marker so Loaded firing more
    // than once for the same instance (e.g. if it's ever removed and re-added to the visual tree)
    // can't attach the drag handlers twice.
    private void TaskListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _viewModel.UpdateSelectedTasks(TaskListBox.SelectedItems.Cast<TaskItem>());

    private void TaskListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-clicking an item that's already part of a multi-selection should act on the
        // whole selection (standard Explorer behavior), not collapse it down to just this row.
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);

        if (element is not ListBoxItem { DataContext: TaskItem task }) return;
        if (TaskListBox.SelectedItems.Contains(task)) return;
        TaskListBox.SelectedItem = task;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.SelectedTaskDetail is null) return;

        // Shortcuts:
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ExportNote_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.K && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            AddTable_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            AddPhoto_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            AddLink_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            AddChecklist_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            AddFile_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;

        // 1. Files copied from Windows File Explorer (via Ctrl+C in Explorer)
        if (Clipboard.ContainsFileDropList())
        {
            var dropList = Clipboard.GetFileDropList();
            if (dropList is not null && dropList.Count > 0)
            {
                foreach (string? path in dropList)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
                    if (TaskDetailViewModel.ImageExtensions.Contains(ext))
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.UriSource = new Uri(path, UriKind.Absolute);
                            bmp.EndInit();
                            bmp.Freeze();
                            RichTextBoxBehavior.InsertInlineImage(NoteEditor, bmp);
                        }
                        catch (NotSupportedException ex)
                        {
                            App.LogException(ex);
                            System.Diagnostics.Debug.WriteLine($"Unsupported image format: {path}");
                        }
                        catch (System.IO.FileNotFoundException ex)
                        {
                            App.LogException(ex);
                            System.Diagnostics.Debug.WriteLine($"Image file not found: {path}");
                        }
                    }
                    else
                    {
                        RichTextBoxBehavior.InsertInlineFileChip(NoteEditor, path);
                    }
                }
                e.Handled = true;
                return;
            }
        }

        // 2. Image copied to clipboard (Snipping Tool, screenshot, browser copy image)
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is not null)
            {
                RichTextBoxBehavior.InsertInlineImage(NoteEditor, image);
                e.Handled = true;
                return;
            }
        }

        // 3. Bare URL copied to clipboard becomes an inline hyperlink
        if (Clipboard.ContainsText() && TryGetBareUrl(Clipboard.GetText(), out var url))
        {
            if (Keyboard.FocusedElement is not TextBox)
            {
                RichTextBoxBehavior.InsertInlineHyperlink(NoteEditor, url);
                e.Handled = true;
                return;
            }
        }

        // 4. Regular Text: if focus is NOT inside an active editable control, focus the editor and paste
        if (Clipboard.ContainsText())
        {
            if (Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not RichTextBox)
            {
                NoteEditor.Focus();
                NoteEditor.Paste();
                e.Handled = true;
            }
        }
    }

    private void NoteBody_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void NoteBody_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void NoteBody_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void NoteBody_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null || files.Length == 0) return;

        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;

            var ext = Path.GetExtension(file)?.ToLowerInvariant() ?? string.Empty;
            if (TaskDetailViewModel.ImageExtensions.Contains(ext))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(file, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    RichTextBoxBehavior.InsertInlineImage(NoteEditor, bmp);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("MainWindow", $"Failed to insert dropped image '{file}'", ex);
                }
            }
            else
            {
                RichTextBoxBehavior.InsertInlineFileChip(NoteEditor, file);
            }
        }
        e.Handled = true;
    }

    private static bool TryGetBareUrl(string text, out string url)
    {
        url = text.Trim();
        if (url.Length == 0 || url.Contains('\n') || url.Contains(' '))
        {
            url = string.Empty;
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;

        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate("https://" + url, UriKind.Absolute, out _))
            return true;

        url = string.Empty;
        return false;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void AlwaysOnTop_Checked(object sender, RoutedEventArgs e) => Topmost = true;

    private void AlwaysOnTop_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;

    private void Photo_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoteBlock { Type: NoteBlockType.Photo } block }) return;
        if (!System.IO.File.Exists(block.PhotoPath)) return;
        new PhotoViewerWindow(block.PhotoPath) { Owner = this }.Show();
    }

    private void TextBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb) SpellCheckContextMenu.RepositionCaret(tb, e);
    }

    private void TextBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is TextBox tb) SpellCheckContextMenu.MergeSuggestions(tb);
    }

    private void RichTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        _activeEditor = rtb;
        FormattingGroup.IsEnabled = true;
        FormattingGroup.Visibility = Visibility.Visible;
        FormattingGroupSeparator.Visibility = Visibility.Visible;
        UpdateFormatButtonStates();
    }

    private void RichTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        _activeEditor = rtb;
        UpdateFormatButtonStates();
    }

    // Now that the text block has no border marking where its content ends, most of its (much
    // taller than the actual typed content) area is blank space - RichTextBox's own click
    // handling only places the caret for clicks on or near actual text, unlike a plain TextBox,
    // which snaps to the nearest position anywhere in its bounds. GetPositionFromPoint with
    // snapToText:false first checks whether the default handling already has this covered, so
    // clicks on real text still go through RichTextBox's normal caret-placement/drag-select/
    // double-click-word-select behavior untouched - only a genuinely blank-space click gets
    // redirected to the nearest valid position (typically the end of the last line), the way
    // clicking anywhere on a Word page does.
    private void RichTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;

        var point = e.GetPosition(rtb);
        AppLogger.Debug("MainWindow", $"PreviewMouseLeftButtonDown at ({point.X:F1}, {point.Y:F1}), Source: {e.OriginalSource?.GetType().Name}");

        // 1. Direct geometric bounds check over all BlockUIContainer children (file cards, images)
        foreach (var bui in rtb.Document.Blocks.OfType<BlockUIContainer>())
        {
            if (bui.Child is FrameworkElement elem && elem.IsVisible)
            {
                try
                {
                    // A. Check if user clicked the '✕' delete button on a file card
                    if (elem is Grid g && elem.Tag is string filePath && !string.IsNullOrWhiteSpace(filePath) && elem.Tag as string != "ImageContainer")
                    {
                        var delBtn = g.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == "DeleteAttachmentBtn");
                        if (delBtn is not null && delBtn.IsVisible)
                        {
                            var dbBounds = delBtn.TransformToAncestor(rtb).TransformBounds(new Rect(0, 0, delBtn.ActualWidth, delBtn.ActualHeight));
                            if (dbBounds.Contains(point))
                            {
                                rtb.Document.Blocks.Remove(bui);
                                RichTextBoxBehavior.SaveContentToBlock(rtb);
                                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                                AppLogger.Info("MainWindow", $"Deleted attachment '{filePath}' via ✕ click");
                                e.Handled = true;
                                return;
                            }
                        }

                        // Check double-click to open file card
                        var cardBounds = elem.TransformToAncestor(rtb).TransformBounds(new Rect(0, 0, elem.ActualWidth, elem.ActualHeight));
                        if (cardBounds.Contains(point))
                        {
                            if (e.ClickCount >= 2)
                            {
                                AppLogger.Info("MainWindow", $"Direct double-click on file attachment '{filePath}'");
                                RichTextBoxBehavior.OpenFileTarget(filePath);
                            }
                            e.Handled = true;
                            return;
                        }
                    }

                    // B. Check if user clicked an Image or Image delete button
                    if (elem is Grid imgGrid && (imgGrid.Tag as string == "ImageContainer" || imgGrid.Children.OfType<Border>().Any(b => b.Child is Image)))
                    {
                        var delImgBtn = imgGrid.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == "DeleteImageBtn");
                        var imgBorder = imgGrid.Children.OfType<Border>().FirstOrDefault(b => b.Child is Image);
                        var image = imgBorder?.Child as Image;

                        if (delImgBtn is not null && delImgBtn.IsVisible)
                        {
                            var dbBounds = delImgBtn.TransformToAncestor(rtb).TransformBounds(new Rect(0, 0, delImgBtn.ActualWidth, delImgBtn.ActualHeight));
                            if (dbBounds.Contains(point))
                            {
                                rtb.Document.Blocks.Remove(bui);
                                RichTextBoxBehavior.SaveContentToBlock(rtb);
                                try
                                {
                                    if (image?.Source is BitmapImage bi && bi.UriSource is { IsAbsoluteUri: true, Scheme: "file" } uri)
                                    {
                                        if (File.Exists(uri.LocalPath)) File.Delete(uri.LocalPath);
                                    }
                                }
                                catch { }
                                AppLogger.Info("MainWindow", "Deleted photo via ✕ click");
                                e.Handled = true;
                                return;
                            }
                        }

                        // Check click on image to expand
                        if (imgBorder is not null && imgBorder.IsVisible)
                        {
                            var imgBounds = imgBorder.TransformToAncestor(rtb).TransformBounds(new Rect(0, 0, imgBorder.ActualWidth, imgBorder.ActualHeight));
                            if (imgBounds.Contains(point))
                            {
                                if (image?.Source is BitmapSource bmp)
                                {
                                    AppLogger.Info("MainWindow", "Expanding photo in PhotoViewerWindow");
                                    var viewer = new PhotoViewerWindow(bmp) { Owner = Application.Current.MainWindow };
                                    viewer.Show();
                                }
                                e.Handled = true;
                                return;
                            }
                        }
                    }

                    // C. Legacy border card or image
                    if (elem is Border legacyCard && legacyCard.Tag is string legPath && !string.IsNullOrWhiteSpace(legPath) && legacyCard.Child is not Image)
                    {
                        var bounds = legacyCard.TransformToAncestor(rtb).TransformBounds(new Rect(0, 0, legacyCard.ActualWidth, legacyCard.ActualHeight));
                        if (bounds.Contains(point))
                        {
                            if (e.ClickCount >= 2)
                                RichTextBoxBehavior.OpenFileTarget(legPath);
                            e.Handled = true;
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MainWindow", $"Block hit test error: {ex.Message}");
                }
            }
        }

        // 2. Visual tree walk for hit elements
        var hitElement = e.OriginalSource as DependencyObject ?? rtb.InputHitTest(point) as DependencyObject;
        while (hitElement is not null && hitElement != rtb)
        {
            if (hitElement is FrameworkElement fe && fe.Tag is string filePath && !string.IsNullOrWhiteSpace(filePath) && fe.Tag as string != "ImageContainer")
            {
                if (e.ClickCount >= 2)
                {
                    AppLogger.Info("MainWindow", $"Visual tree double-click hit on file attachment '{filePath}' ({fe.GetType().Name})");
                    RichTextBoxBehavior.OpenFileTarget(filePath);
                }
                e.Handled = true;
                return;
            }

            if (hitElement is Image img && img.Source is BitmapSource bmp)
            {
                AppLogger.Info("MainWindow", "Visual tree hit on photo -> expanding viewer");
                var viewer = new PhotoViewerWindow(bmp) { Owner = Application.Current.MainWindow };
                viewer.Show();
                e.Handled = true;
                return;
            }

            if (hitElement is System.Windows.Controls.Primitives.ButtonBase or CheckBox)
                return;

            hitElement = hitElement is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(hitElement)
                : LogicalTreeHelper.GetParent(hitElement);
        }

        var textPos = rtb.GetPositionFromPoint(point, snapToText: false);
        if (textPos is not null) return;

        var snapped = rtb.GetPositionFromPoint(point, snapToText: true);
        if (snapped is null) return;

        rtb.Focus();
        rtb.CaretPosition = snapped;
        e.Handled = true;
    }

    // NoteBody is the Grid wrapping BlockItemsControl (the whole text/photo/link/checklist
    // stream) and carries a large MinHeight so the pane always reads as a big open page, even
    // when the only content is one empty Text block. Most of that reserved height is genuinely
    // blank - no block renders anywhere near it - so a click there won't hit any block's own
    // controls; it hits NoteBody's own Background (Transparent, not left null, specifically so
    // it stays hit-test visible). This is a Preview/tunneling handler, so it also runs for
    // clicks that land on real content further down the tree; the OriginalSource check is what
    // tells the two cases apart - only a click whose innermost hit is NoteBody itself (nothing
    // more specific underneath the cursor) counts as "blank space", and in that case we send
    // focus to the end of the last text block, the way clicking below the last paragraph in a
    // Word document lands the caret at the end of the document.
    private void NoteBody_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        NoteEditor.Focus();
        NoteEditor.CaretPosition = NoteEditor.Document.ContentEnd;
        e.Handled = true;
    }

    private void TitleTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NoteEditor.Focus();
            NoteEditor.CaretPosition = NoteEditor.Document.ContentStart;
            e.Handled = true;
        }
    }

    private void TitleTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // No action needed - just here to handle events
    }

    private static FrameworkElement? FindFirstVisualDescendantWithContextMenu(DependencyObject root)
    {
        if (root is FrameworkElement { ContextMenu: not null } self) return self;
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var found = FindFirstVisualDescendantWithContextMenu(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindFirstVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null) return null;
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            var nested = FindFirstVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindLastVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        T? found = null;
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                found = match;

            var nested = FindLastVisualDescendant<T>(child);
            if (nested is not null)
                found = nested;
        }
        return found;
    }

    // Fires when focus leaves this RichTextBox for ANY reason, including moving to another
    // RichTextBox - defer the check a tick so the new focus target has actually been set before
    // deciding whether to disable the group.
    private void RichTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Keyboard.FocusedElement is RichTextBox) return;
            _activeEditor = null;
            FormattingGroup.IsEnabled = false;
            FormattingGroup.Visibility = Visibility.Collapsed;
            FormattingGroupSeparator.Visibility = Visibility.Collapsed;
        }), DispatcherPriority.Background);
    }

    private void RichTextBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;

        var point = e.GetPosition(rtb);
        foreach (var bui in rtb.Document.Blocks.OfType<BlockUIContainer>())
        {
            if (bui.Child is FrameworkElement elem && elem.Tag is string && elem is not Image)
            {
                if (elem.IsVisible)
                {
                    try
                    {
                        var transform = elem.TransformToAncestor(rtb);
                        var bounds = transform.TransformBounds(new Rect(0, 0, elem.ActualWidth, elem.ActualHeight));
                        if (bounds.Contains(point))
                        {
                            // The ContextMenu (Save As/Open/Delete, etc.) actually lives on an inner
                            // element - e.g. CreateImageContainer sets it on the Image, CreateFileCard
                            // on the inner "CardBody" Border - not on this outer wrapper Grid, so
                            // checking elem.ContextMenu directly always came up null and right-clicks
                            // fell through to the RichTextBox's default select-on-right-click instead.
                            var menuOwner = FindFirstVisualDescendantWithContextMenu(elem);
                            if (menuOwner?.ContextMenu is not null)
                            {
                                menuOwner.ContextMenu.PlacementTarget = menuOwner;
                                menuOwner.ContextMenu.IsOpen = true;
                                e.Handled = true;
                                return;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        SpellCheckContextMenu.RepositionCaret(rtb, e);
    }

    // The formatting ContextMenu (Bold/Italic/Font Size/.../Numbered List) replaces WPF's
    // automatic spelling-suggestions menu entirely - setting any custom ContextMenu suppresses the
    // built-in one. Merge suggestions back in manually whenever the right-click lands on a
    // misspelled word (PreviewMouseRightButtonDown above moves the caret there first).
    private void RichTextBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;

        var point = Mouse.GetPosition(rtb);
        foreach (var bui in rtb.Document.Blocks.OfType<BlockUIContainer>())
        {
            if (bui.Child is FrameworkElement elem && (elem.Tag is string || elem is Border { Child: Image } || elem is Image))
            {
                if (elem.IsVisible)
                {
                    try
                    {
                        var transform = elem.TransformToAncestor(rtb);
                        var bounds = transform.TransformBounds(new Rect(0, 0, elem.ActualWidth, elem.ActualHeight));
                        if (bounds.Contains(point))
                        {
                            e.Handled = true;
                            return;
                        }
                    }
                    catch { }
                }
            }
        }

        SpellCheckContextMenu.MergeSuggestions(rtb);
    }

    private void RichTextBox_QueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;

        var point = Mouse.GetPosition(rtb);
        foreach (var bui in rtb.Document.Blocks.OfType<BlockUIContainer>())
        {
            if (bui.Child is FrameworkElement elem && elem.IsVisible)
            {
                try
                {
                    var transform = elem.TransformToAncestor(rtb);
                    var bounds = transform.TransformBounds(new Rect(0, 0, elem.ActualWidth, elem.ActualHeight));
                    if (bounds.Contains(point))
                    {
                        // Check if over delete button
                        if (elem is Grid g)
                        {
                            var delBtn = g.Children.OfType<FrameworkElement>().FirstOrDefault(c => (c.Tag as string) is "DeleteAttachmentBtn" or "DeleteImageBtn");
                            if (delBtn is not null && delBtn.IsVisible)
                            {
                                var dbBounds = delBtn.TransformToAncestor(rtb).TransformBounds(new Rect(0, 0, delBtn.ActualWidth, delBtn.ActualHeight));
                                if (dbBounds.Contains(point))
                                {
                                    e.Cursor = Cursors.Hand;
                                    e.Handled = true;
                                    return;
                                }
                            }
                        }

                        // Over photo -> Hand cursor (indicating clickable / expandable)
                        if (elem.Tag as string == "ImageContainer" || (elem is Grid imgG && imgG.Children.OfType<Border>().Any(b => b.Child is Image)))
                        {
                            e.Cursor = Cursors.Hand;
                            e.Handled = true;
                            return;
                        }

                        // Over file attachment card -> Hand cursor
                        if (elem.Tag is string filePath && !string.IsNullOrWhiteSpace(filePath))
                        {
                            e.Cursor = Cursors.Hand;
                            e.Handled = true;
                            return;
                        }

                        e.Cursor = elem.Cursor ?? Cursors.Arrow;
                        e.Handled = true;
                        return;
                    }
                }
                catch { }
            }
        }
    }

    private void RichTextBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;

        var point = e.GetPosition(rtb);
        foreach (var bui in rtb.Document.Blocks.OfType<BlockUIContainer>())
        {
            if (bui.Child is FrameworkElement elem && elem.IsVisible)
            {
                try
                {
                    var transform = elem.TransformToAncestor(rtb);
                    var bounds = transform.TransformBounds(new Rect(0, 0, elem.ActualWidth, elem.ActualHeight));
                    if (bounds.Contains(point))
                    {
                        Mouse.OverrideCursor = Cursors.Hand;
                        return;
                    }
                }
                catch { }
            }
        }

        if (Mouse.OverrideCursor == Cursors.Hand)
            Mouse.OverrideCursor = null;
    }

    private void UpdateFormatButtonStates()
    {
        if (_activeEditor is null || BoldButton is null) return;

        var active = (Brush)FindResource("HighlightBrush");

        var isBold = _activeEditor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w == FontWeights.Bold;
        BoldButton.Background = isBold ? active : Brushes.Transparent;

        var isItalic = _activeEditor.Selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s && s == FontStyles.Italic;
        ItalicButton.Background = isItalic ? active : Brushes.Transparent;

        var isUnderline = _activeEditor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection { Count: > 0 } decorations
                           && decorations[0].Location == TextDecorationLocation.Underline;
        UnderlineButton.Background = isUnderline ? active : Brushes.Transparent;
    }

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleBold.Execute(null, _activeEditor);
        UpdateFormatButtonStates();
    }

    private void Italic_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleItalic.Execute(null, _activeEditor);
        UpdateFormatButtonStates();
    }

    private void Underline_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleUnderline.Execute(null, _activeEditor);
        UpdateFormatButtonStates();
    }

    private void Bullets_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleBullets.Execute(null, _activeEditor);

    private void Numbering_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleNumbering.Execute(null, _activeEditor);

    private void ListStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            if (tag == "Bullets") EditingCommands.ToggleBullets.Execute(null, _activeEditor);
            else if (tag == "Numbering") EditingCommands.ToggleNumbering.Execute(null, _activeEditor);
        }
        combo.SelectedIndex = -1;
    }

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeEditor is null) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item } || item.Content is not string name) return;
        _activeEditor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(name));
    }

    private void FontSize_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEditor is null) return;
        if ((sender as FrameworkElement)?.Tag is not string tag || !double.TryParse(tag, out var size)) return;
        _activeEditor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
    }

    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEditor is null) return;
        if ((sender as FrameworkElement)?.Tag is not string hex) return;
        _activeEditor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, ToBrush(hex));
    }

    private void HighlightColor_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEditor is null) return;
        if ((sender as FrameworkElement)?.Tag is not string hex) return;
        _activeEditor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, ToBrush(hex));
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeEditor is null) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item }) return;
        if (double.TryParse(item.Content?.ToString(), out var size))
            _activeEditor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
    }

    private void TextColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeEditor is null) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string hex } }) return;
        _activeEditor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, ToBrush(hex));
    }

    private void HighlightCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeEditor is null) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string hex } }) return;
        _activeEditor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, ToBrush(hex));
    }

    private void OpenDebugLog_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("MainWindow", "User requested to open debug log file");
        AppLogger.OpenLogFile();
    }

    private static Brush ToBrush(string hex)
        => hex == "Transparent" ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(hex)!;
}
