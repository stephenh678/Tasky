using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TodoApp.Behaviors;

/// <summary>
/// Visual adorner rendered during drag-and-drop task reordering.
/// Draws a clean horizontal accent guide line and bullet at the drop insertion position.
/// </summary>
public class TaskDropAdorner : Adorner
{
    private bool _isAfter;
    private static readonly Brush IndicatorBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xF5));
    private static readonly Pen IndicatorPen = new(IndicatorBrush, 2);

    static TaskDropAdorner()
    {
        IndicatorBrush.Freeze();
        IndicatorPen.Freeze();
    }

    public TaskDropAdorner(UIElement adornedElement, bool isAfter) : base(adornedElement)
    {
        _isAfter = isAfter;
        IsHitTestVisible = false;
    }

    public bool IsAfter
    {
        get => _isAfter;
        set
        {
            if (_isAfter != value)
            {
                _isAfter = value;
                InvalidateVisual();
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double y = _isAfter ? AdornedElement.RenderSize.Height : 0;
        double width = AdornedElement.RenderSize.Width;

        // Draw horizontal guide line
        dc.DrawLine(IndicatorPen, new Point(6, y), new Point(width - 6, y));

        // Draw small bullet dot on the left
        dc.DrawEllipse(IndicatorBrush, IndicatorPen, new Point(6, y), 3, 3);
    }
}
