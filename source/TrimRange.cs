using Wpf = System.Windows;
using C = System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;
using M = System.Windows.Media;

namespace ClipIt;

public sealed class TrimRange : C.Canvas
{
    private readonly C.Border rail = new() { Height = 5, CornerRadius = new Wpf.CornerRadius(2), Background = B("#293C57") };
    private readonly C.Border selection = new() { Height = 7, CornerRadius = new Wpf.CornerRadius(3), Background = B("#4289FF") };
    private readonly C.Border playhead = new() { Width = 2, Height = 44, Background = M.Brushes.White, IsHitTestVisible = false };
    private readonly Thumb left = Handle("Trim start"), right = Handle("Trim end");
    public double Duration { get; private set; }
    public double Start { get; private set; }
    public double End { get; private set; }
    public event EventHandler? RangeChanged;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubEnded;
    public bool IsAdjusting => scrubbing || left.IsDragging || right.IsDragging;
    private double position;
    private bool scrubbing;
    public TrimRange()
    {
        Height = 58; Background = M.Brushes.Transparent; Focusable = true;
        Children.Add(rail); Children.Add(selection); Children.Add(playhead); Children.Add(left); Children.Add(right);
        SizeChanged += (_, _) => Layout();
        left.DragDelta += (_, e) => MoveHandle(true, Delta(e.HorizontalChange));
        right.DragDelta += (_, e) => MoveHandle(false, Delta(e.HorizontalChange));
        left.KeyDown += (_, e) => MoveKey(e, true); right.KeyDown += (_, e) => MoveKey(e, false);
        MouseLeftButtonDown += (_, e) =>
        {
            if (Duration <= 0) return;
            double x = e.GetPosition(this).X;
            Focus();
            if (!CaptureMouse()) return;
            scrubbing = true;
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            SeekAt(x);
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!scrubbing) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndScrub(); return; }
            SeekAt(e.GetPosition(this).X); e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!scrubbing) return;
            SeekAt(e.GetPosition(this).X); EndScrub(); e.Handled = true;
        };
        LostMouseCapture += (_, _) => EndScrub();
        KeyDown += (_, e) =>
        {
            if (Duration <= 0 || e.Key is not (Key.Left or Key.Right)) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : .05;
            SeekRequested?.Invoke(this, Math.Clamp(position + (e.Key == Key.Left ? -step : step), 0, Duration));
            e.Handled = true;
        };
    }
    private void SeekAt(double x)
    {
        double next = Math.Clamp((x - 8) / Math.Max(1, ActualWidth - 16) * Duration, 0, Duration);
        SetPosition(next); SeekRequested?.Invoke(this, next);
    }
    public void EndScrub()
    {
        if (!scrubbing) return;
        scrubbing = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        ScrubEnded?.Invoke(this, EventArgs.Empty);
    }
    public void Setup(double duration) { Duration = duration; Start = position = 0; End = duration; Layout(); }
    public void SetRange(double start, double end)
    {
        if (Duration < .1) return;
        Start = Math.Clamp(start, 0, Math.Max(0, Duration - .1));
        End = Math.Clamp(end, Math.Min(Start + .1, Duration), Duration);
        Layout(); RangeChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetPosition(double value) { position = value; Layout(); }
    private double Delta(double pixels) => pixels / Math.Max(1, ActualWidth - 16) * Duration;
    private double X(double value) => 8 + (Duration > 0 ? Math.Clamp(value / Duration, 0, 1) : 0) * Math.Max(0, ActualWidth - 16);
    private void Layout()
    {
        rail.Width = Math.Max(0, ActualWidth - 16); SetLeft(rail, 8); SetTop(rail, 27);
        selection.Width = Math.Max(0, X(End) - X(Start)); SetLeft(selection, X(Start)); SetTop(selection, 26);
        SetLeft(left, X(Start) - 7); SetLeft(right, X(End) - 7); SetTop(left, 12); SetTop(right, 12);
        SetLeft(playhead, X(position) - 1); SetTop(playhead, 6);
        left.ToolTip = "Start · " + MediaTools.Time(Start); right.ToolTip = "End · " + MediaTools.Time(End);
    }
    private void MoveKey(KeyEventArgs e, bool start)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : .05;
        if (e.Key is not (Key.Left or Key.Right)) return;
        if (e.Key == Key.Left) step = -step;
        MoveHandle(start, step);
        e.Handled = true;
    }
    private void MoveHandle(bool start, double step)
    {
        if (Duration < .1) return;
        if (start) SetRange(Math.Min(Start + step, End - .1), End);
        else SetRange(Start, End + step);
    }
    private static Thumb Handle(string name)
    {
        var border = new Wpf.FrameworkElementFactory(typeof(C.Border));
        border.SetValue(C.Border.BackgroundProperty, B("#72AAFF"));
        border.SetValue(C.Border.CornerRadiusProperty, new Wpf.CornerRadius(4));
        border.SetValue(C.Border.BorderBrushProperty, B("#BBD8FF"));
        border.SetValue(C.Border.BorderThicknessProperty, new Wpf.Thickness(1));
        var thumb = new Thumb { Width = 14, Height = 34, Cursor = Cursors.SizeWE, Focusable = true, Template = new C.ControlTemplate(typeof(Thumb)) { VisualTree = border } };
        System.Windows.Automation.AutomationProperties.SetName(thumb, name);
        return thumb;
    }
    private static M.Brush B(string hex) => (M.Brush)new M.BrushConverter().ConvertFromString(hex)!;
}
