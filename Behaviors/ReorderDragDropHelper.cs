using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TodoApp.Adorners;

namespace TodoApp.Behaviors;

// Hand-rolled drag-to-reorder for a plain ItemsControl - WPF has no built-in reordering the way
// some newer frameworks do. One instance wires up a single ItemsControl; used twice in this app
// (the note-block stream and, separately, each checklist's item list), each with its own move
// callback into that collection's ObservableCollection<T>.Move(oldIndex, newIndex).
public class ReorderDragDropHelper
{
    private const string Format = "TaskyReorderItem";

    private readonly ItemsControl _itemsControl;
    private readonly Action<int, int> _move;
    private Point? _dragStartPoint;
    private InsertionLineAdorner? _adorner;
    private AdornerLayer? _adornerLayer;

    public ReorderDragDropHelper(ItemsControl itemsControl, Action<int, int> move)
    {
        _itemsControl = itemsControl;
        _move = move;

        _itemsControl.AllowDrop = true;
        _itemsControl.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        _itemsControl.PreviewMouseMove += OnPreviewMouseMove;
        _itemsControl.PreviewMouseLeftButtonUp += (_, _) => _dragStartPoint = null;
        _itemsControl.DragOver += OnDragOver;
        _itemsControl.DragLeave += (_, _) => RemoveAdorner();
        _itemsControl.Drop += OnDrop;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        _dragStartPoint = StartedFromHandle(source) ? e.GetPosition(_itemsControl) : null;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(_itemsControl);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var container = FindContainerAt(start);
        _dragStartPoint = null;
        if (container?.DataContext is not { } item) return;

        DragDrop.DoDragDrop(_itemsControl, new DataObject(Format, item), DragDropEffects.Move);
        RemoveAdorner();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(Format)) return;

        var container = FindContainerAt(e.GetPosition(_itemsControl));
        if (container is null) { RemoveAdorner(); return; }

        var before = e.GetPosition(container).Y < container.ActualHeight / 2;
        ShowAdorner(container, before);
        e.Effects = DragDropEffects.Move;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        RemoveAdorner();
        if (!e.Data.GetDataPresent(Format) || e.Data.GetData(Format) is not { } dragged) return;

        var container = FindContainerAt(e.GetPosition(_itemsControl));
        if (container?.DataContext is not { } target) return;

        // IndexOf naturally guards against cross-list drops (e.g. a note block dragged over a
        // checklist's own item list) - an item that doesn't belong to THIS control's collection
        // simply isn't found, and there's nothing to move.
        var sourceIndex = _itemsControl.Items.IndexOf(dragged);
        var targetIndex = _itemsControl.Items.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return;

        var before = e.GetPosition(container).Y < container.ActualHeight / 2;
        var insertAt = before ? targetIndex : targetIndex + 1;
        if (insertAt > sourceIndex) insertAt--; // account for the source item's own removal shifting later indices down

        _move(sourceIndex, insertAt);
    }

    private static bool StartedFromHandle(DependencyObject source)
    {
        for (var el = source; el is not null; el = GetParent(el))
        {
            if (el is FrameworkElement fe && DragHandleBehavior.GetIsHandle(fe)) return true;
        }
        return false;
    }

    private FrameworkElement? FindContainerAt(Point point)
    {
        if (_itemsControl.InputHitTest(point) is not DependencyObject hit) return null;
        for (var el = hit; el is not null; el = GetParent(el))
        {
            if (el is FrameworkElement fe && ItemsControl.ItemsControlFromItemContainer(fe) == _itemsControl)
                return fe;
        }
        return null;
    }

    // VisualTreeHelper only accepts Visual/Visual3D - but a click inside the note editor's
    // RichTextBox can report a FlowDocument-internal object (Run, Paragraph) as its source, and
    // those are FrameworkContentElements, not part of the visual tree at all. Fall back to the
    // logical tree for those; it still bridges back up to the RichTextBox itself, which IS a
    // Visual, so the walk can resume normally from there.
    private static DependencyObject? GetParent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private void ShowAdorner(FrameworkElement container, bool before)
    {
        var layer = AdornerLayer.GetAdornerLayer(_itemsControl);
        if (layer is null) return;

        if (_adornerLayer != layer || _adorner is null)
        {
            RemoveAdorner();
            _adorner = new InsertionLineAdorner(_itemsControl, (Brush)Application.Current.Resources["AccentBrush"]);
            _adornerLayer = layer;
            layer.Add(_adorner);
        }

        var offset = container.TranslatePoint(new Point(0, before ? 0 : container.ActualHeight), _itemsControl);
        _adorner.Y = offset.Y;
        _adorner.InvalidateVisual();
    }

    private void RemoveAdorner()
    {
        if (_adorner is null) return;
        _adornerLayer?.Remove(_adorner);
        _adorner = null;
        _adornerLayer = null;
    }
}
