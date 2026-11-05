using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows.Interop;
using C = System.Windows.Controls;
using Wpf = System.Windows;

namespace ClipIt;

internal static class DesktopChecks
{
    // This check runs only on explicit request, with disposable settings/media and a temporary registry key.
    internal static int Run(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "desktop-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLIPIT_DATA", data);
        var results = new List<string>();
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        ClipWindow? window = null;
        int exit = 1;
        void Require(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(name); }
        app.Startup += async (_, _) =>
        {
            try
            {
                var initial = Settings.Load();
                Require(initial.Modifiers == 1 && initial.Key == (uint)Keys.B, "New profiles default to Alt+B");
                Require(CaptureQuality.Resolutions(900).SequenceEqual(new[] { 720, 900 }) && CaptureQuality.Resolutions(1440).Last() == 1440 && CaptureQuality.Resolutions(1080).All(h => h <= 1080), "Resolution options include the monitor maximum and exclude larger resolutions");
                Require(CaptureQuality.RecommendedBitrate(1440, 60) > CaptureQuality.RecommendedBitrate(1080, 60) && CaptureQuality.RecommendedBitrate(1080, 60) > CaptureQuality.RecommendedBitrate(1080, 30), "Suggested bitrate increases with resolution and frame rate");
                Require(initial.ReplaySeconds == 120, "Existing settings retain the two-minute default");
                foreach (int seconds in Enumerable.Range(1, 10).Select(n => n * 30)) (initial with { ReplaySeconds = seconds }).Validated();
                foreach (int seconds in new[] { 0, 29, 31, 301, 330 })
                {
                    bool rejected = false;
                    try { (initial with { ReplaySeconds = seconds }).Validated(); } catch (ArgumentException) { rejected = true; }
                    Require(rejected, $"Invalid replay length {seconds}s is rejected");
                }
                Require(!initial.StartWithWindows && !initial.StartMinimized && initial.Overlay, "Existing settings safely default to manual startup and enabled overlay");
                var settings = initial with { Folder = Path.Combine(data, "clips"), ReplaySeconds = 30, GameWindow = ":ClipItWaiting:__clipit_no_game__.exe", StartWithWindows = true, StartMinimized = true, Modifiers = 3, Key = 118 };
                settings.Save();
                Require(Settings.Load() == settings, "Startup, minimized and overlay preferences survive reload");
                string registryPath = @"Software\ClipIt.Checks." + Guid.NewGuid().ToString("N");
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(registryPath);
                    string executable = Path.Combine(data, "path with spaces", "ClipIt.exe");
                    StartupRegistration.WriteEntry(key, "ClipIt", true, executable);
                    Require((string?)key.GetValue("ClipIt") == $"\"{executable}\" --windows-startup", "Windows startup command quotes paths and carries the sign-in argument");
                    StartupRegistration.WriteEntry(key, "ClipIt", false, executable);
                    Require(key.GetValue("ClipIt") == null, "Turning off startup removes its registration");
                }
                finally { Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false); }

                window = new ClipWindow { ShowInTaskbar = false };
                app.MainWindow = window;
                new WindowInteropHelper(window).EnsureHandle();
                var replayLength = (C.ComboBox)window.FindName("ReplayLengthBox");
                var bitrate = (C.TextBox)window.FindName("BitrateBox");
                var estimate = (C.TextBlock)window.FindName("EstimatedSizeLabel");
                Require(replayLength.Items.Count == 10 && (string)replayLength.Items[0] == "0:30" && (string)replayLength.Items[9] == "5:00" && replayLength.SelectedIndex == 0, "Replay dropdown offers every 30-second step and restores the saved selection");
                string shortEstimate = estimate.Text;
                replayLength.SelectedIndex = 9;
                Require(estimate.Text != shortEstimate && Settings.Load().ReplaySeconds == 30, "File-size estimate updates before applying the replay length");
                bitrate.Text = "50";
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().ReplaySeconds == 300 && Settings.Load().Bitrate == 50000, "Apply saves the five-minute replay length and selected bitrate");
                bitrate.Text = "24"; replayLength.SelectedIndex = 0;
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().ReplaySeconds == 30 && estimate.Text == shortEstimate, "Replay length and estimate return to the thirty-second configuration");
                var quality = (C.ComboBox)window.FindName("QualityBox");
                var fps = (C.ComboBox)window.FindName("FpsBox");
                int originalQuality = quality.SelectedIndex;
                bitrate.Text = "19";
                replayLength.SelectedIndex = 1;
                Require(bitrate.Text == "19", "Editing replay length preserves a manually entered bitrate");
                replayLength.SelectedIndex = 0;
                string manualEstimate = estimate.Text;
                quality.SelectedIndex = quality.SelectedIndex == 0 ? quality.Items.Count - 1 : 0;
                Require(bitrate.Text != "19" && estimate.Text != manualEstimate, "Resolution selection replaces the manual bitrate and updates estimated file size");
                bitrate.Text = "19"; fps.SelectedIndex = 0;
                Require(bitrate.Text != "19", "Frame-rate changes restore a suggested bitrate");
                foreach (var control in new C.ComboBox[] { quality, fps, replayLength, (C.ComboBox)window.FindName("MonitorBox"), (C.ComboBox)window.FindName("GameAppBox"), (C.ComboBox)window.FindName("MicrophoneBox") })
                {
                    int selected = control.SelectedIndex;
                    var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent };
                    control.RaiseEvent(wheel);
                    Require(wheel.Handled && control.SelectedIndex == selected, $"Mouse wheel cannot change {control.Name}");
                }
                quality.SelectedIndex = originalQuality; fps.SelectedIndex = 1; bitrate.Text = "24";
                Require(window.SaveShortcut(3, 116) && Settings.Load().Modifiers == 3 && Settings.Load().Key == 116, "A new shortcut registers and persists immediately without Apply settings");
                nint windowHandle = new WindowInteropHelper(window).Handle;
                Require(Win32.RegisterHotKey(windowHandle, 99, 3 | 0x4000, 117), "Reserved a test hotkey to check conflict handling");
                try { Require(!window.SaveShortcut(3, 117) && Settings.Load().Key == 116, "A conflicting shortcut leaves the saved and active shortcut intact"); }
                finally { Win32.UnregisterHotKey(windowHandle, 99); }
                Require(!window.IsVisible && !window.ShowInTaskbar, "Tray-only startup creates the hotkey window without displaying it");
                Require(!((C.CheckBox)window.FindName("StartWindowsToggle")).IsEnabled, "Development instances cannot overwrite installed startup registration");
                Require(((Wpf.FrameworkElement)window.FindName("StartMinimizedRow")).Visibility == Wpf.Visibility.Visible, "Start minimized is visible when startup is enabled");
                var overlayToggle = (C.CheckBox)window.FindName("OverlayToggle");
                overlayToggle.IsChecked = false;
                Require(!Settings.Load().Overlay && Settings.Load().StartMinimized, "Overlay saves immediately while preserving startup preferences");
                overlayToggle.IsChecked = true;
                Require(Settings.Load().Overlay, "Overlay can be re-enabled and persisted");
                using var editor = new ClipEditor();
                var game = (C.CheckBox)editor.FindName("GameEnabled");
                var discord = (C.CheckBox)editor.FindName("DiscordEnabled");
                var mic = (C.CheckBox)editor.FindName("MicrophoneEnabled");
                var currentMix = typeof(ClipEditor).GetMethod("CurrentMix", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Require(game.IsChecked == true && discord.IsChecked == true && mic.IsChecked == true && (MixSettings)currentMix.Invoke(editor, null)! == new MixSettings(), "Editor audio switches start enabled with audible source gains");
                discord.IsChecked = false;
                var mix = (MixSettings)currentMix.Invoke(editor, null)!;
                Require(mix.DiscordMuted && mix.DiscordGain == 0 && mix.GameGain == 1 && mix.MicrophoneGain == 1, "Turning off Discord mutes only that source");
                discord.IsChecked = true;
                Require(((MixSettings)currentMix.Invoke(editor, null)!).DiscordGain == 1, "Turning audio back on restores the source");

                var overlay = new RecordingOverlay();
                try
                {
                    overlay.Update(true, false, "");
                    nint hwnd = new WindowInteropHelper(overlay).Handle;
                    long styles = GetWindowLongPtr(hwnd, -20);
                    Require(overlay.IsVisible && !overlay.ShowInTaskbar && !overlay.ShowActivated && (styles & 0x080000A0) == 0x080000A0, "Overlay is visible, click-through, non-activating and absent from taskbar");
                    Require(GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == 0x11, "Overlay is excluded from Windows capture");
                    overlay.Update(false, false, "");
                    Require(!overlay.IsVisible, "Turning overlay off hides its window");
                }
                finally { overlay.Close(); }

                var supervisor = (Supervisor)typeof(ClipWindow).GetField("recorder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                for (int i = 0; i < 90 && !supervisor.Recording; i++) await Task.Delay(500);
                Require(supervisor.Recording && !window.IsVisible, "Hidden startup starts recording without showing the application window");
                for (int i = 0; i < 90 && supervisor.Seconds < 30; i++) await Task.Delay(500);
                Require(supervisor.Seconds == 30, "Recorder heartbeat fills and caps at the selected thirty-second length");
                await Task.Delay(4000);
                string saved = "";
                supervisor.Message += message => { if (message.Type == "saved") saved = message.Path; };
                supervisor.Save();
                for (int i = 0; i < 90 && saved.Length == 0; i++) await Task.Delay(500);
                Require(File.Exists(saved), "Tray-only instance confirms a successfully captured clip");
                var captured = await MediaTools.Probe(saved, CancellationToken.None);
                Require(captured.Duration >= 29 && captured.Duration <= 35, "A filled thirty-second replay saves the selected duration with keyframe allowance");
                exit = 0;
            }
            catch (Exception ex) { results.Add("FAILED: " + ex); }
            finally
            {
                if (window != null) typeof(ClipWindow).GetMethod("Quit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                app.Shutdown();
            }
        };
        app.Run();
        File.WriteAllText(Path.Combine(root, "desktop-check-results.json"), JsonSerializer.Serialize(new { Success = exit == 0, Data = data, Checks = results }, new JsonSerializerOptions { WriteIndented = true }));
        return exit;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
