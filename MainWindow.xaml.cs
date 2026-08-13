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

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        ApplySavedWindowState();
        ThemeService.ApplyTitleBar(this);

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

        _viewModel.Tray.NewTaskRequested += () => Dispatcher.Invoke(ShowQuickAdd);
        _viewModel.Tray.ShowRequested += () => Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
        _viewModel.Tray.ExitRequested += () => Dispatcher.Invoke(Close);

        Closing += (_, _) =>
        {
            _viewModel.FlushPendingSave();
            var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            _viewModel.SaveWindowState(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
            _viewModel.Shutdown();
        };
    }

    // Applies the saved window size/position before the window is shown. Only repositions if the
    // saved point still falls within the current virtual screen, so a monitor that's since been
    // unplugged can't strand the window off-screen.
    private void ApplySavedWindowState()
    {
        Width = _viewModel.SavedWindowWidth;
        Height = _viewModel.SavedWindowHeight;

        if (_viewModel.SavedWindowLeft is { } left && _viewModel.SavedWindowTop is { } top &&
            left >= SystemParameters.VirtualScreenLeft && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top >= SystemParameters.VirtualScreenTop && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            Left = left;
            Top = top;
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
        if (_viewModel.SelectedTaskDetail is not { } detail) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var images = paths.Where(p => TaskDetailViewModel.ImageExtensions
            .Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
        var files = paths.Except(images).ToList();

        if (images.Count > 0) detail.AddPhotosFromPaths(images);
        if (files.Count > 0) detail.AddFilesFromPaths(files);
    }

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
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (_viewModel.SelectedTaskDetail is not { } detail) return;

        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is null) return;
            detail.AddPhotoFromClipboardImage(image);
            e.Handled = true;
            return;
        }

        // A bare URL (nothing else on the clipboard) becomes a link block, the same shortcut
        // photos already get. Pasting a URL as part of a sentence still pastes as plain text -
        // this only fires when the whole clipboard is just the one URL. Right-click Paste inside
        // a text block still bypasses this if you specifically want a URL as literal text.
        if (Clipboard.ContainsText() && TryGetBareUrl(Clipboard.GetText(), out var url))
        {
            detail.AddLinkFromUrl(url);
            e.Handled = true;
        }
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

    private void About_Click(object sender, RoutedEventArgs e)
    {
        ThemedMessageBox.Show(
            "Tasky\nA simple task manager with notes, links, photos, due dates, and tags.",
            "About Tasky", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AlwaysOnTop_Checked(object sender, RoutedEventArgs e) => Topmost = true;

    private void AlwaysOnTop_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;

    private void Photo_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoteBlock { Type: NoteBlockType.Photo } block }) return;
        if (!System.IO.File.Exists(block.PhotoPath)) return;
        new PhotoViewerWindow(block.PhotoPath) { Owner = this }.Show();
    }

    private void RichTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        _activeEditor = rtb;
        UpdateFormatButtonStates();
    }

    private void RichTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        _activeEditor = rtb;
        UpdateFormatButtonStates();
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

    private static Brush ToBrush(string hex)
        => hex == "Transparent" ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(hex)!;
}
