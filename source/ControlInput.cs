using System.Windows;
using System.Windows.Controls;
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
        // A wheel gesture scrolls the containing page, never the value of a control under the pointer.
        e.Handled = true;
        for (DependencyObject? parent = (DependencyObject)sender; parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer)
            {
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 120.0 * SystemParameters.WheelScrollLines * 16);
                return;
            }
    }
}
