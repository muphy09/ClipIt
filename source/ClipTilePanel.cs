using Wpf = System.Windows;
using C = System.Windows.Controls;

namespace ClipIt;

public sealed class ClipTilePanel : C.Panel
{
    private const double TargetWidth = 260;
    private const double Gap = 12;

    internal static (int Columns, Wpf.Size Tile) Layout(double width)
    {
        int columns = Math.Max(1, (int)((width + Gap) / (TargetWidth + Gap)));
        double tileWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        return (columns, new Wpf.Size(tileWidth, tileWidth * 9 / 16 + 64));
    }

    internal static (int Start, int End) VisibleItems(double width, double top, double height, int count)
    {
        var (columns, tile) = Layout(width);
        double rowHeight = tile.Height + Gap;
        int start = (int)Math.Floor(top / rowHeight) * columns;
        int end = (int)Math.Ceiling((top + height) / rowHeight) * columns;
        return (start, Math.Min(count, end));
    }

    protected override Wpf.Size MeasureOverride(Wpf.Size available)
    {
        double width = double.IsInfinity(available.Width) ? TargetWidth : available.Width;
        var (columns, tile) = Layout(width);
        foreach (Wpf.UIElement child in InternalChildren) child.Measure(tile);
        int rows = (InternalChildren.Count + columns - 1) / columns;
        return new Wpf.Size(width, rows * tile.Height + Math.Max(0, rows - 1) * Gap);
    }

    protected override Wpf.Size ArrangeOverride(Wpf.Size finalSize)
    {
        var (columns, tile) = Layout(finalSize.Width);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            double x = i % columns * (tile.Width + Gap);
            double y = i / columns * (tile.Height + Gap);
            InternalChildren[i].Arrange(new Wpf.Rect(new Wpf.Point(x, y), tile));
        }
        return finalSize;
    }
}
