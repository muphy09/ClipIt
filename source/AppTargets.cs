using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipIt;

public sealed record CaptureApp(string Token, string Name, string Executable)
{
    public override string ToString() => Name;
    public static CaptureApp Auto => new("auto", "Automatic · full-screen game", "");
}

internal static class AppTargets
{
    private delegate bool EnumWindow(nint hwnd, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32")] private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32")] private static extern bool IsIconic(nint window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int length);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int length);
    [DllImport("kernel32")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32")] private static extern bool CloseHandle(nint handle);

    public static string Encode(string text) => text.Replace("#", "#22").Replace(":", "#3A");
    public static string Executable(string token) => token.Split(':').LastOrDefault()?.Replace("#3A", ":").Replace("#22", "#") ?? "";
    private static CaptureApp? Read(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        string exe;
        try { var file = new StringBuilder(2048); int size = file.Capacity; if (!QueryFullProcessImageName(process, 0, file, ref size)) return null; exe = Path.GetFileName(file.ToString()); }
        finally { CloseHandle(process); }
        var title = new StringBuilder(1024); GetWindowText(window, title, title.Capacity);
        var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
        if (title.Length == 0 || cls.Length == 0 || exe.Equals("ClipIt.exe", StringComparison.OrdinalIgnoreCase)) return null;
        string label = Path.GetFileNameWithoutExtension(exe);
        return new($"{Encode(title.ToString())}:{Encode(cls.ToString())}:{Encode(exe)}", label, exe);
    }
    public static List<CaptureApp> List()
    {
        var list = new List<CaptureApp>();
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsWindowVisible(window)) return true;
                var app = Read(window);
                if (app != null && !app.Executable.StartsWith("Discord", StringComparison.OrdinalIgnoreCase) && !app.Executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && list.All(a => !a.Executable.Equals(app.Executable, StringComparison.OrdinalIgnoreCase))) list.Add(app);
            }
            catch { }
            return true;
        }, 0);
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    internal static nint FindWindow(string token)
    {
        nint match = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || Read(window)?.Token != token) return true;
            match = window;
            return false;
        }, 0);
        return match;
    }
    internal static List<CaptureApp> RecordingWindows()
    {
        var windows = new List<CaptureApp>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || Read(window) is not { } app || app.Executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                return true;
            if (windows.Any(existing => existing.Token == app.Token)) return true;
            string title = app.Token.Split(':', 2)[0].Replace("#3A", ":").Replace("#22", "#");
            windows.Add(app with { Name = $"{app.Name} · {title}" });
            return true;
        }, 0);
        return windows.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    internal static bool HasWindow(nint window) => GetWindowThreadProcessId(window, out _) != 0;
    internal static (int Width, int Height) WindowSize(nint window) =>
        GetClientRect(window, out var bounds) ? (bounds.Right, bounds.Bottom) : (0, 0);
    internal static System.Drawing.Rectangle WindowCaptureBounds(nint window)
    {
        Point origin = default;
        if (window == 0 || !IsWindowVisible(window) || IsIconic(window)
            || !GetClientRect(window, out var bounds) || !ClientToScreen(window, ref origin))
            return System.Drawing.Rectangle.Empty;
        return new(origin.X, origin.Y, bounds.Right, bounds.Bottom);
    }
    public static CaptureApp? FullscreenGame(string monitor = "")
    {
        var window = GetForegroundWindow();
        var app = Read(window);
        if (app == null) return null;
        string[] excluded = ["clipit", "discord", "explorer", "codex", "chatgpt", "chrome", "msedge", "firefox", "steam", "steamwebhelper", "epicgameslauncher", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "obs64", "vlc"];
        string name = Path.GetFileNameWithoutExtension(app.Executable);
        if (excluded.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase))) return null;
        if (!GetWindowRect(window, out var r)) return null;
        var display = Screen.FromHandle(window);
        if (monitor.Length > 0 && !display.DeviceName.Equals(monitor, StringComparison.OrdinalIgnoreCase)) return null;
        var screen = display.Bounds;
        if (r.Right - r.Left < screen.Width * .98 || r.Bottom - r.Top < screen.Height * .98) return null;
        return app;
    }
    public static CaptureApp FromToken(string token) { string exe = Executable(token); return new(token, Path.GetFileNameWithoutExtension(exe), exe); }
    public static CaptureApp? Discord(string preferred)
    {
        CaptureApp? found = null;
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsWindowVisible(window)) return true;
                var app = Read(window);
                if (app != null && (app.Executable.Equals(preferred, StringComparison.OrdinalIgnoreCase) || (preferred.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase) && new[] { "DiscordPTB.exe", "DiscordCanary.exe" }.Contains(app.Executable, StringComparer.OrdinalIgnoreCase)))) { found = app; return false; }
            }
            catch { }
            return true;
        }, 0);
        return found;
    }
    public static string FolderName(string app)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(app.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray());
        safe = safe.Trim().TrimEnd('.'); if (safe.Length > 80) safe = safe[..80];
        if (safe.Length == 0 || safe is "." or "..") safe = "Desktop";
        string head = safe.Split('.')[0].ToUpperInvariant();
        if (head is "CON" or "PRN" or "AUX" or "NUL" || System.Text.RegularExpressions.Regex.IsMatch(head, "^(COM|LPT)[1-9]$")) safe = "_" + safe;
        return safe;
    }
}
