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
    private const uint ExcludeFromCapture = 0x11;
    private readonly TextBlock symbol = new() { FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = Wpf.HorizontalAlignment.Center };
    private readonly Border badge;
    private readonly StackPanel captureStatus = new() { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock captureTime = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
    private readonly System.Windows.Shapes.Ellipse captureDot = new() { Width = 12, Height = 12, Stroke = Brushes.Black, StrokeThickness = 1.4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) };
    private readonly Border captureBadge;
    private readonly StackPanel layout = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
    private static readonly SolidColorBrush ActiveDot = new(Color.FromRgb(246, 72, 79));
    private static readonly SolidColorBrush IdleDot = new(Color.FromRgb(145, 151, 162));
    private DateTime flashUntil;
    private nint handle;
    private bool excludedFromCapture;

    internal RecordingOverlay()
    {
        Width = 22; Height = 22; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
        Topmost = true; Focusable = false; IsHitTestVisible = false;
        badge = new Border { Width = 22, Height = 22, Background = new SolidColorBrush(Color.FromArgb(215, 14, 21, 33)), CornerRadius = new CornerRadius(4), Child = symbol };
        captureStatus.Children.Add(captureTime);
        captureStatus.Children.Add(captureDot);
        captureBadge = new Border { Height = 22, Background = badge.Background, CornerRadius = new CornerRadius(4), Child = captureStatus };
        layout.Children.Add(captureBadge);
        layout.Children.Add(badge);
        Content = layout;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE.
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x080000A0);
            excludedFromCapture = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                && SetWindowDisplayAffinity(handle, ExcludeFromCapture)
                && GetWindowDisplayAffinity(handle, out uint affinity) && affinity == ExcludeFromCapture;
            if (!excludedFromCapture) Log.Write("The overlay is hidden because Windows could not exclude it from capture.");
        };
    }

    internal void FlashSaved() => flashUntil = DateTime.UtcNow.AddSeconds(1.8);

    internal void Update(bool enabled, bool recording, string monitor, bool captureEnabled = false, bool captureActive = false,
        bool capturePaused = false, string? time = null, string captureMonitor = "")
    {
        if (!enabled && !captureEnabled) { if (IsVisible) Hide(); return; }
        // Apply capture exclusion while the native window is still hidden.
        if (handle == 0) new WindowInteropHelper(this).EnsureHandle();
        if (!excludedFromCapture) return;
        bool flash = DateTime.UtcNow < flashUntil;
        symbol.Text = recording || flash ? "✓" : "✕";
        symbol.Foreground = recording || flash ? new SolidColorBrush(Color.FromRgb(85, 217, 171)) : new SolidColorBrush(Color.FromRgb(247, 119, 119));
        badge.Background = new SolidColorBrush(flash ? Color.FromArgb(240, 18, 82, 58) : Color.FromArgb(215, 14, 21, 33));
        badge.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        captureBadge.Visibility = captureEnabled ? Visibility.Visible : Visibility.Collapsed;
        captureBadge.Margin = new Thickness(0, 0, enabled ? 5 : 0, 0);
        captureDot.Fill = captureActive ? ActiveDot : IdleDot;
        captureTime.Text = captureActive && capturePaused ? time == null ? "Paused" : time + " · Paused" : time ?? "";
        captureTime.Visibility = captureTime.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        layout.Measure(new Wpf.Size(double.PositiveInfinity, 22));
        Width = Math.Ceiling(layout.DesiredSize.Width);
        if (!IsVisible) Show();
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == (enabled ? monitor : captureMonitor)) ?? System.Windows.Forms.Screen.PrimaryScreen!;
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
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
