using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PromptQueue.App.Controls;

/// <summary>
/// A lightweight drag "ghost": paints a semi-transparent snapshot of the row
/// being dragged and follows the mouse vertically inside the task list.
/// </summary>
public sealed class RowDragAdorner : Adorner
{
    private readonly Rectangle _ghost;
    private double _offsetY;
    private readonly double _left;

    public RowDragAdorner(UIElement adornedElement, ImageSource snapshot,
        double width, double height, double left)
        : base(adornedElement)
    {
        _left = left;
        _ghost = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new ImageBrush(snapshot) { Stretch = Stretch.Fill },
            Opacity = 0.75,
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.4,
                Direction = 270,
            },
            IsHitTestVisible = false,
        };
        AddVisualChild(_ghost);
        IsHitTestVisible = false;
    }

    /// <summary>Vertical position (in adorned-element coordinates) of the ghost's top edge.</summary>
    public void UpdatePosition(double y)
    {
        _offsetY = y;
        InvalidateArrange();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _ghost;

    protected override Size MeasureOverride(Size constraint)
    {
        _ghost.Measure(constraint);
        return _ghost.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ghost.Arrange(new Rect(new Point(_left, _offsetY),
            new Size(_ghost.Width, _ghost.Height)));
        return finalSize;
    }
}
