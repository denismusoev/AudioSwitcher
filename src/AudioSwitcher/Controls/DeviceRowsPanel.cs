using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AudioSwitcher.Controls;

// Keeps every list row at its own content-driven height while scrolling by
// complete row boundaries. Additional content therefore expands only its row.
public sealed class DeviceRowsPanel : Panel, IScrollInfo
{
    private const double MinimumRowHeight = 56;
    private const double PixelScrollStep = 16;
    private readonly List<double> rowHeights = [];
    private readonly List<double> rowOffsets = [];
    private double contentHeight;
    private double maxOffset;
    private double verticalOffset;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentHeight { get; private set; }
    public double ExtentWidth => ViewportWidth;
    public double ViewportHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset => verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        int previousAnchor = RowAt(verticalOffset);
        double previousAnchorDelta = previousAnchor >= 0 && previousAnchor < rowOffsets.Count
            ? verticalOffset - rowOffsets[previousAnchor]
            : 0;

        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 480;
        rowHeights.Clear();
        rowOffsets.Clear();
        contentHeight = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            rowOffsets.Add(contentHeight);
            double height = Math.Max(MinimumRowHeight, child.DesiredSize.Height);
            rowHeights.Add(height);
            contentHeight += height;
        }

        double availableHeight = double.IsFinite(availableSize.Height) ? availableSize.Height : contentHeight;
        ViewportWidth = width;
        ViewportHeight = CompleteRowsViewport(availableHeight);
        maxOffset = LastPageOffset();
        ExtentHeight = Math.Max(contentHeight, maxOffset + ViewportHeight);

        if (previousAnchor >= 0 && previousAnchor < rowOffsets.Count)
        {
            double restored = rowOffsets[previousAnchor];
            if (rowHeights[previousAnchor] > ViewportHeight)
                restored += previousAnchorDelta;
            verticalOffset = Math.Clamp(restored, 0, maxOffset);
        }
        else verticalOffset = Math.Clamp(verticalOffset, 0, maxOffset);

        ScrollOwner?.InvalidateScrollInfo();
        return new Size(width, Math.Min(ViewportHeight, contentHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(0, rowOffsets[i] - verticalOffset, finalSize.Width, rowHeights[i]));
        return finalSize;
    }

    private double CompleteRowsViewport(double availableHeight)
    {
        if (rowHeights.Count == 0 || availableHeight <= 0) return Math.Max(0, availableHeight);
        double completeHeight = 0;
        foreach (double height in rowHeights)
        {
            if (completeHeight + height > availableHeight) break;
            completeHeight += height;
        }
        return completeHeight > 0 ? completeHeight : availableHeight;
    }

    private double LastPageOffset()
    {
        if (contentHeight <= ViewportHeight || rowOffsets.Count == 0) return 0;
        for (int i = 0; i < rowOffsets.Count; i++)
        {
            if (contentHeight - rowOffsets[i] <= ViewportHeight)
                return rowOffsets[i];
        }
        return Math.Max(0, contentHeight - ViewportHeight);
    }

    private int RowAt(double offset)
    {
        if (rowOffsets.Count == 0) return -1;
        for (int i = rowOffsets.Count - 1; i >= 0; i--)
        {
            if (offset >= rowOffsets[i] - 0.01) return i;
        }
        return 0;
    }

    private double SnapOffset(double offset)
    {
        double clamped = Math.Clamp(offset, 0, maxOffset);
        if (Math.Abs(clamped - maxOffset) < 0.01) return maxOffset;
        int index = RowAt(clamped);
        if (index < 0 || rowHeights[index] > ViewportHeight) return clamped;

        double current = rowOffsets[index];
        double next = index + 1 < rowOffsets.Count ? Math.Min(rowOffsets[index + 1], maxOffset) : maxOffset;
        return clamped - current < next - clamped ? current : next;
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        double requestedOffset = SnapOffset(offset);
        if (Math.Abs(requestedOffset - verticalOffset) < 0.01) return;
        verticalOffset = requestedOffset;
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child != null && VisualTreeHelper.GetParent(child) != this)
            child = VisualTreeHelper.GetParent(child);
        if (child is not UIElement element) return Rect.Empty;

        int index = InternalChildren.IndexOf(element);
        if (index < 0 || index >= rowOffsets.Count) return Rect.Empty;

        Rect content;
        if (rowHeights[index] > ViewportHeight)
        {
            if (rectangle.IsEmpty) return Rect.Empty;
            content = visual.TransformToAncestor(this).TransformBounds(rectangle);
            content.Offset(0, verticalOffset);
        }
        else content = new Rect(0, rowOffsets[index], ViewportWidth, rowHeights[index]);

        if (content.Top < verticalOffset)
            SetVerticalOffset(content.Top);
        else if (content.Bottom > verticalOffset + ViewportHeight)
            SetVerticalOffset(rowHeights[index] <= ViewportHeight ? rowOffsets[index] : content.Bottom - ViewportHeight);

        var visible = content;
        visible.Offset(0, -verticalOffset);
        visible.Intersect(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return visible;
    }

    public void LineUp()
    {
        int index = RowAt(verticalOffset);
        if (index >= 0 && rowHeights[index] > ViewportHeight && verticalOffset > rowOffsets[index])
            SetVerticalOffset(verticalOffset - PixelScrollStep);
        else if (index > 0)
            SetVerticalOffset(rowOffsets[index - 1]);
        else SetVerticalOffset(0);
    }

    public void LineDown()
    {
        int index = RowAt(verticalOffset);
        if (index >= 0 && rowHeights[index] > ViewportHeight && verticalOffset < rowOffsets[index] + rowHeights[index] - ViewportHeight)
            SetVerticalOffset(verticalOffset + PixelScrollStep);
        else if (index >= 0 && index + 1 < rowOffsets.Count)
            SetVerticalOffset(rowOffsets[index + 1]);
        else SetVerticalOffset(maxOffset);
    }

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    private double WheelStep => SystemParameters.WheelScrollLines < 0 ? ViewportHeight : SystemParameters.WheelScrollLines * PixelScrollStep;
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
