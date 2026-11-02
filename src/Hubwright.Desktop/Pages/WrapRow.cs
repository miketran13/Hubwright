using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// A row that wraps: children sit side by side and move to the next line when the width runs out,
/// so buttons and labels in a narrow column are never cut off. Each child is measured against the
/// full width, so long text wraps inside it instead of running past the edge. Children keep their
/// own vertical alignment within a line (see <see cref="Ui.BottomAligned"/>).
/// </summary>
public sealed partial class WrapRow : Panel
{
    public double Spacing { get; set; } = 8;

    public double LineSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        var size = Layout(availableSize.Width, null);
        return new Size(double.IsInfinity(availableSize.Width) ? size.Width : Math.Min(size.Width, availableSize.Width), size.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, (child, rect) => child.Arrange(rect));
        return finalSize;
    }

    /// <summary>Places the children in lines; with <paramref name="place"/> null it only measures.</summary>
    private Size Layout(double width, Action<UIElement, Rect>? place)
    {
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        double y = 0, widest = 0;
        var start = 0;
        while (start < visible.Count)
        {
            // Fill one line: always at least one child, then as many as fit.
            var end = start;
            double lineWidth = 0, lineHeight = 0;
            while (end < visible.Count)
            {
                var desired = visible[end].DesiredSize;
                var next = end == start ? desired.Width : lineWidth + Spacing + desired.Width;
                if (end > start && next > width)
                {
                    break;
                }

                lineWidth = next;
                lineHeight = Math.Max(lineHeight, desired.Height);
                end++;
            }

            if (place is not null)
            {
                double x = 0;
                for (var i = start; i < end; i++)
                {
                    var desired = visible[i].DesiredSize;
                    var w = double.IsInfinity(width) ? desired.Width : Math.Min(desired.Width, Math.Max(0, width - x));
                    place(visible[i], new Rect(x, y, w, lineHeight));
                    x += w + Spacing;
                }
            }

            widest = Math.Max(widest, lineWidth);
            y += lineHeight + (end < visible.Count ? LineSpacing : 0);
            start = end;
        }

        // Collapsed children still need an arrange pass.
        if (place is not null)
        {
            foreach (var hidden in Children.Where(c => c.Visibility != Visibility.Visible))
            {
                place(hidden, default);
            }
        }

        return new Size(widest, y);
    }
}
