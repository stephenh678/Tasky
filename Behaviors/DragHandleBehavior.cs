using System.Windows;

namespace TodoApp.Behaviors;

// Marks an element as a drag handle for ReorderDragDropHelper - a drag gesture only starts from
// an element carrying this, so clicking into a checklist checkbox, a text box, or a remove button
// inside a block never gets hijacked into a reorder drag.
public static class DragHandleBehavior
{
    public static readonly DependencyProperty IsHandleProperty =
        DependencyProperty.RegisterAttached("IsHandle", typeof(bool), typeof(DragHandleBehavior), new PropertyMetadata(false));

    public static bool GetIsHandle(DependencyObject obj) => (bool)obj.GetValue(IsHandleProperty);
    public static void SetIsHandle(DependencyObject obj, bool value) => obj.SetValue(IsHandleProperty, value);
}
