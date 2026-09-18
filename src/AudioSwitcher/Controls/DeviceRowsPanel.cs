using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AudioSwitcher.Controls;

// Keep compact, content-sized slots and a viewport containing complete rows.
// Spare space stays below the list instead of stretching the device cards.
public sealed class DeviceRowsPanel : Panel, IScrollInfo
{
    private double rowHeight = 56;
    private double verticalOffset;
    private int visibleRows = 1;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentHeight { get; private set; }
    public double ExtentWidth => ViewportWidth;
    public double ViewportHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset => verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }
    private double MaxOffset => Math.Max(0, ExtentHeight - ViewportHeight);
    private bool OversizedRows => rowHeight > ViewportHeight;
    private double LineStep => OversizedRows ? 16 : rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 480;
        double minimumHeight = 56;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            minimumHeight = Math.Max(minimumHeight, child.DesiredSize.Height);
        }
        double viewport = double.IsFinite(availableSize.Height) ? availableSize.Height : minimumHeight * InternalChildren.Count;
        double previousRow = verticalOffset / rowHeight;
        visibleRows = Math.Max(1, (int)Math.Floor(viewport / minimumHeight));
        rowHeight = minimumHeight;
        ViewportWidth = width;
        ViewportHeight = viewport >= minimumHeight ? visibleRows * rowHeight : viewport;
        ExtentHeight = InternalChildren.Count * rowHeight;
        verticalOffset = Math.Clamp((OversizedRows ? previousRow : Math.Round(previousRow)) * rowHeight, 0, MaxOffset);
        ScrollOwner?.InvalidateScrollInfo();
        return new Size(width, Math.Min(ViewportHeight, ExtentHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(0, i * rowHeight - verticalOffset, finalSize.Width, rowHeight));
        return finalSize;
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        double requestedOffset = Math.Clamp(OversizedRows ? offset : Math.Round(offset / rowHeight, MidpointRounding.AwayFromZero) * rowHeight, 0, MaxOffset);
        if (requestedOffset == verticalOffset) return;
        verticalOffset = requestedOffset;
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child != null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        if (child is not UIElement element) return Rect.Empty;
        int index = InternalChildren.IndexOf(element);
        if (index < 0) return Rect.Empty;
        // A row normally fits in its slot. Oversized wrapped content needs pixel
        // scrolling and the caller's rectangle, so its lower text stays reachable.
        Rect content;
        if (OversizedRows)
        {
            if (rectangle.IsEmpty) return Rect.Empty;
            content = visual.TransformToAncestor(this).TransformBounds(rectangle);
            content.Offset(0, verticalOffset);
        }
        else content = new Rect(0, index * rowHeight, ViewportWidth, rowHeight);
        if (content.Top < verticalOffset || content.Height > ViewportHeight)
            SetVerticalOffset(content.Top);
        else if (content.Bottom > verticalOffset + ViewportHeight)
            SetVerticalOffset(content.Bottom - ViewportHeight);
        var visible = content;
        visible.Offset(0, -verticalOffset);
        visible.Intersect(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return visible;
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineStep);
    public void LineDown() => SetVerticalOffset(VerticalOffset + LineStep);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    private double WheelStep => SystemParameters.WheelScrollLines < 0 ? ViewportHeight : SystemParameters.WheelScrollLines * LineStep;
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + WheelStep);
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
}
