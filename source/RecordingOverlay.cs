using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf = System.Windows;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace ClipIt;

// A normal desktop window only: no game injection, graphics hooks, drivers, or process access.
internal sealed class RecordingOverlay : Wpf.Window
{
    private readonly TextBlock symbol = new() { FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = Wpf.HorizontalAlignment.Center };
    private readonly Border badge;
    private DateTime flashUntil;
    private nint handle;

    internal RecordingOverlay()
    {
        Width = 22; Height = 22; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
        Topmost = true; Focusable = false; IsHitTestVisible = false;
        badge = new Border { Background = new SolidColorBrush(Color.FromArgb(215, 14, 21, 33)), CornerRadius = new CornerRadius(4), Child = symbol };
        Content = badge;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE.
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x080000A0);
            // Avoid recording our own status badge (Windows 10 2004+).
            if (!SetWindowDisplayAffinity(handle, 0x11)) Log.Write("Overlay capture exclusion is unavailable.");
        };
    }

    internal void FlashSaved() => flashUntil = DateTime.UtcNow.AddSeconds(1.8);

    internal void Update(bool enabled, bool recording, string monitor)
    {
        if (!enabled) { if (IsVisible) Hide(); return; }
        bool flash = DateTime.UtcNow < flashUntil;
        symbol.Text = recording || flash ? "✓" : "✕";
        symbol.Foreground = recording || flash ? new SolidColorBrush(Color.FromRgb(85, 217, 171)) : new SolidColorBrush(Color.FromRgb(247, 119, 119));
        badge.Background = new SolidColorBrush(flash ? Color.FromArgb(240, 18, 82, 58) : Color.FromArgb(215, 14, 21, 33));
        if (!IsVisible) Show();
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == monitor) ?? System.Windows.Forms.Screen.PrimaryScreen!;
        // Screen rectangles are physical pixels; SetWindowPos avoids mixed-DPI coordinate errors.
        var scale = System.Windows.PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        int width = (int)Math.Round(Width * scale.M11), height = (int)Math.Round(Height * scale.M22);
        var area = screen.WorkingArea;
        // Align above the clock area rather than hugging the bottom-right screen corner.
        int rightInset = (int)Math.Round(28 * scale.M11), bottomInset = (int)Math.Round(52 * scale.M22);
        SetWindowPos(handle, new nint(-1), area.Right - width - rightInset, area.Bottom - height - bottomInset, width, height, 0x0010);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
