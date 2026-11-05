using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;

namespace ClipIt;

internal static class ControlInput
{
    private static bool installed;
    internal static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(ComboBox), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(ScrollPage));
        EventManager.RegisterClassHandler(typeof(Slider), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(ScrollPage));
    }

    private static void ScrollPage(object sender, MouseWheelEventArgs e)
    {
        // An open popup owns the wheel, including when its list is already at either end.
        e.Handled = true;
        if (sender is ComboBox { IsDropDownOpen: true } combo)
        {
            if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: { } child } && FindScrollViewer(child) is { } list)
                Scroll(list, e.Delta);
            return;
        }
        // Closed controls pass scrolling to the page without editing their selected value.
        for (DependencyObject? parent = (DependencyObject)sender; parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer)
            {
                Scroll(viewer, e.Delta);
                return;
            }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return null;
    }

    private static void Scroll(ScrollViewer viewer, int delta)
    {
        int lines = SystemParameters.WheelScrollLines;
        double step = lines < 0 ? viewer.ViewportHeight : lines * (viewer.CanContentScroll ? 1 : 16);
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - delta / 120.0 * step);
    }
}
