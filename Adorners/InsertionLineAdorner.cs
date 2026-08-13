using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TodoApp.Adorners;

// Draws a thin horizontal accent line across the adorned element at a given Y offset - used by
// ReorderDragDropHelper to show where a dragged block or checklist item will land, without
// touching the actual item containers (which have inconsistent root element types across the
// different block templates).
public class InsertionLineAdorner : Adorner
{
    private readonly Pen _pen;

    public double Y { get; set; }

    public InsertionLineAdorner(UIElement adornedElement, Brush brush) : base(adornedElement)
    {
        _pen = new Pen(brush, 3);
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = AdornedElement is FrameworkElement fe ? fe.ActualWidth : 0;
        drawingContext.DrawLine(_pen, new Point(0, Y), new Point(width, Y));
    }
}
