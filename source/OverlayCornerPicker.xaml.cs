using Wpf = System.Windows;

namespace ClipIt;

public enum OverlayCorner { BottomLeft, BottomRight, TopLeft, TopRight }

public partial class OverlayCornerPicker : Wpf.Controls.UserControl
{
    private bool restoringSelection;

    public OverlayCornerPicker()
    {
        InitializeComponent();
        foreach (var button in new[] { TopLeft, TopRight, BottomLeft, BottomRight })
            button.Checked += (_, _) =>
            {
                if (!restoringSelection) CornerChanged?.Invoke(this, EventArgs.Empty);
            };
    }

    internal event EventHandler? CornerChanged;

    internal OverlayCorner SelectedCorner
    {
        get => TopLeft.IsChecked == true ? OverlayCorner.TopLeft
            : TopRight.IsChecked == true ? OverlayCorner.TopRight
            : BottomLeft.IsChecked == true ? OverlayCorner.BottomLeft
            : OverlayCorner.BottomRight;
        set
        {
            restoringSelection = true;
            try
            {
                (value switch
                {
                    OverlayCorner.TopLeft => TopLeft,
                    OverlayCorner.TopRight => TopRight,
                    OverlayCorner.BottomLeft => BottomLeft,
                    _ => BottomRight
                }).IsChecked = true;
            }
            finally { restoringSelection = false; }
        }
    }
}
