using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using C = System.Windows.Controls;
using Wpf = System.Windows;

namespace ClipIt;

internal static class DesktopChecks
{
    // This check runs only on explicit request, with disposable settings/media and a temporary registry key.
    internal static int Run(string root, bool recordingChecks = true, bool recordingOnly = false)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "desktop-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLIPIT_DATA", data);
        var results = new List<string>();
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        ClipWindow? window = null;
        Wpf.Window? overlayBackground = null;
        int exit = 1;
        void Require(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(name); }
        app.Startup += async (_, _) =>
        {
            try
            {
                var initial = Settings.Load();
                Require(initial.Modifiers == 1 && initial.Key == (uint)Keys.B, "New profiles default to Alt+B");
                Require(initial.LibraryModifiers == 5 && initial.LibraryKey == (uint)Keys.B, "Your Clips defaults to Alt+Shift+B for existing and new profiles");
                Require(initial.RecordingShortcut == "Alt+V", "Recording defaults to Alt+V");
                Require(ClipWindow.RecordingMessage(true, true, 20, 120) == "Replay buffer is building" && ClipWindow.RecordingMessage(true, false, 0, 120) == "Replay buffer is building", "Building and restarting capture show the requested buffer message");
                Require(ClipWindow.RecordingMessage(false, false, 0, 120) == "Recording has been paused.", "Paused recording shows the requested message");
                Require(ClipWindow.RecordingMessage(true, true, 120, 120) == "Recording is active. Last 2 minutes ready for capture." && ClipWindow.RecordingMessage(true, true, 30, 30) == "Recording is active. Last 30 seconds ready for capture." && ClipWindow.RecordingMessage(true, true, 90, 90) == "Recording is active. Last 1 minute 30 seconds ready for capture.", "Active status uses the selected length with correct minute and second units");
                Require(!initial.BufferInMemory, "Disk buffering is the default for new and existing profiles");
                Require(BufferDrive.Warning(BufferDriveKind.HardDisk).Contains("HDD") && BufferDrive.Warning(BufferDriveKind.HardDisk).Contains("RAM") && BufferDrive.Warning(BufferDriveKind.SolidState).Length == 0, "HDD detection warns and recommends RAM; solid-state drives do not show that warning");
                Require(CaptureQuality.Resolutions(900).SequenceEqual(new[] { 720, 900 }) && CaptureQuality.Resolutions(1440).Last() == 1440 && CaptureQuality.Resolutions(1080).All(h => h <= 1080), "Resolution options include the monitor maximum and exclude larger resolutions");
                Require(CaptureQuality.RecommendedBitrate(1440, 60) > CaptureQuality.RecommendedBitrate(1080, 60) && CaptureQuality.RecommendedBitrate(1080, 60) > CaptureQuality.RecommendedBitrate(1080, 30), "Suggested bitrate increases with resolution and frame rate");
                Require(initial.ReplaySeconds == 120, "Existing settings retain the two-minute default");
                foreach (int seconds in Enumerable.Range(1, 20).Select(n => n * 30)) (initial with { ReplaySeconds = seconds }).Validated();
                foreach (int seconds in new[] { 0, 29, 31, 601, 630 })
                {
                    bool rejected = false;
                    try { (initial with { ReplaySeconds = seconds }).Validated(); } catch (ArgumentException) { rejected = true; }
                    Require(rejected, $"Invalid replay length {seconds}s is rejected");
                }
                Require(!initial.StartWithWindows && !initial.StartMinimized && initial.Overlay, "Existing settings safely default to manual startup and enabled overlay");
                var settings = initial with { Folder = Path.Combine(data, "clips"), ReplaySeconds = 30, GameWindow = ":ClipItWaiting:__clipit_no_game__.exe", StartWithWindows = true, StartMinimized = true, GameTileView = true, Modifiers = 3, Key = 118, LibraryModifiers = 3, LibraryKey = 121 };
                settings.Save();
                Require(Settings.Load() == settings, "Startup, minimized and overlay preferences survive reload");
                if (recordingOnly)
                {
                    window = new ClipWindow { ShowInTaskbar = false };
                    app.MainWindow = window;
                    new WindowInteropHelper(window).EnsureHandle();
                    await VerifyRecording(window);
                    exit = 0;
                    return;
                }
                window = new ClipWindow { ShowInTaskbar = false };
                app.MainWindow = window;
                new WindowInteropHelper(window).EnsureHandle();
                VerifyOverlays(window);
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

                var formatted = PatchNotesWindow.FormatNotes("## [v0.5.7]\n\n### Bugfixes\n\n- example\n- Another fix\n  continued\n\n## [v0.5.6]\n\nEarlier changes.");
                var noteBlocks = formatted.Blocks.ToArray();
                Require(noteBlocks.Length == 5 && noteBlocks[0] is System.Windows.Documents.Paragraph version && new System.Windows.Documents.TextRange(version.ContentStart, version.ContentEnd).Text.Trim() == "[v0.5.7]" && version.FontSize > ((System.Windows.Documents.Paragraph)noteBlocks[1]).FontSize, "Patch notes render version and section headings at distinct sizes");
                Require(noteBlocks[2] is System.Windows.Documents.List noteList && noteList.ListItems.Count == 2 && new System.Windows.Documents.TextRange(noteList.ContentStart, noteList.ContentEnd).Text.Contains("Another fix continued"), "Patch notes render bullets, wrapped items and multiple versions");
                var nestedNotes = (System.Windows.Documents.List)PatchNotesWindow.FormatNotes("- Parent\n    - Child\n\t\t- Grandchild\n\t- Sibling\n- Next parent").Blocks.FirstBlock;
                var children = (System.Windows.Documents.List)nestedNotes.ListItems.FirstListItem.Blocks.LastBlock;
                Require(nestedNotes.ListItems.Count == 2 && children.ListItems.Count == 2 && children.ListItems.FirstListItem.Blocks.LastBlock is System.Windows.Documents.List, "Patch notes preserve nested bullets indented with spaces or tabs");
                Require(PatchNotesWindow.FormatNotes("  \n").Blocks.Count == 1 && File.Exists(Path.Combine(AppContext.BaseDirectory, "patchnotes")), "Empty patch notes show a message and the editable notes file is bundled");
                var patchButton = (C.Button)window.FindName("PatchNotesButton");
                patchButton.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                patchButton.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                var notesWindow = app.Windows.OfType<PatchNotesWindow>().Single();
                Require(notesWindow.IsVisible && notesWindow.Owner == window && ((C.FlowDocumentScrollViewer)notesWindow.FindName("NotesViewer")).Document.Blocks.Count > 0, "Settings opens one owned patch notes window with formatted content");
                notesWindow.Close();
                patchButton.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                notesWindow = app.Windows.OfType<PatchNotesWindow>().Single();
                Require(notesWindow.IsVisible, "Patch notes can be reopened after closing");
                notesWindow.Close();
                var replayLength = (C.ComboBox)window.FindName("ReplayLengthBox");
                var bitrate = (C.TextBox)window.FindName("BitrateBox");
                var estimate = (C.TextBlock)window.FindName("EstimatedSizeLabel");
                var pendingBar = (Wpf.FrameworkElement)window.FindName("PendingSettingsBar");
                Require(pendingBar.Visibility == Wpf.Visibility.Collapsed, "Apply bar is absent before any pending capture changes");
                bitrate.Text = "19";
                Require(pendingBar.Visibility == Wpf.Visibility.Visible && ((C.TextBlock)window.FindName("SettingsHint")).Text == "Changes have been made", "Editing capture settings shows the requested Apply bar message");
                bitrate.Text = "24";
                Require(pendingBar.Visibility == Wpf.Visibility.Collapsed, "Reverting capture edits hides the Apply bar");
                bitrate.Text = "1";
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(((Wpf.FrameworkElement)window.FindName("QualityNotice")).Visibility == Wpf.Visibility.Visible && ((Wpf.FrameworkElement)window.FindName("LocationNotice")).Visibility == Wpf.Visibility.Collapsed, "Invalid bitrate warns directly below Capture & Quality");
                bitrate.Text = "24";
                Require(((Wpf.FrameworkElement)window.FindName("QualityNotice")).Visibility == Wpf.Visibility.Collapsed && pendingBar.Visibility == Wpf.Visibility.Collapsed, "Correcting quality clears its warning and pending changes");
                var folderBox = (C.TextBox)window.FindName("FolderBox"); string savedFolder = folderBox.Text;
                folderBox.Text = "";
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(((Wpf.FrameworkElement)window.FindName("LocationNotice")).Visibility == Wpf.Visibility.Visible, "Invalid save folder warns directly below Save Location");
                folderBox.Text = savedFolder;
                Require(((Wpf.FrameworkElement)window.FindName("LocationNotice")).Visibility == Wpf.Visibility.Collapsed && pendingBar.Visibility == Wpf.Visibility.Collapsed, "Restoring the save folder clears its warning and Apply bar");
                Require(replayLength.Items.Count == 20 && (string)replayLength.Items[0] == "0:30" && (string)replayLength.Items[19] == "10:00" && replayLength.SelectedIndex == 0, "Replay dropdown offers every 30-second step and restores the saved selection");
                string shortEstimate = estimate.Text;
                replayLength.SelectedIndex = 19;
                Require(estimate.Text != shortEstimate && Settings.Load().ReplaySeconds == 30, "File-size estimate updates before applying the replay length");
                bitrate.Text = "50";
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().ReplaySeconds == 600 && Settings.Load().Bitrate == 50000, "Apply saves the ten-minute replay length and selected bitrate");
                Require(pendingBar.Visibility == Wpf.Visibility.Collapsed, "Successful Apply hides the pending changes bar");
                bitrate.Text = "24"; replayLength.SelectedIndex = 0;
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().ReplaySeconds == 30 && estimate.Text == shortEstimate, "Replay length and estimate return to the thirty-second configuration");
                var micBoost = (C.Slider)window.FindName("MicrophoneBoost");
                var recordingBoost = (C.Slider)window.FindName("RecordingMicBoost");
                Require(micBoost.Value == 0 && recordingBoost.Value == 0, "Existing profiles start with no microphone boost");
                micBoost.Value = 9;
                Require(Settings.Load().MicrophoneBoostDb == 0 && pendingBar.Visibility == Wpf.Visibility.Visible,
                    "Capture mic boost is pending until Apply");
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().MicrophoneBoostDb == 9 && recordingBoost.Value == 9
                    && Settings.Load().Recording.MicrophoneBoostDb == null, "Recording inherits the applied Capture mic boost");
                recordingBoost.Value = 3;
                micBoost.Value = 0;
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().MicrophoneBoostDb == 0 && Settings.Load().Recording.MicrophoneBoostDb == 3 && recordingBoost.Value == 3,
                    "An explicit Recording mic boost persists independently of Capture");
                string portalRoot = Path.Combine(data, "portal");
                string portalGame = Path.Combine(portalRoot, "myGAME", "Highlights");
                Directory.CreateDirectory(portalGame);
                string portalClip = Path.Combine(portalGame, "Overtime.mp4");
                File.WriteAllText(portalClip, "disposable portal fixture");
                var portalBox = (C.TextBox)window.FindName("PortalFolderBox");
                var settingsPanel = (C.ScrollViewer)window.FindName("GeneralSettingsPanel");
                Require(portalBox.IsDescendantOf((Wpf.DependencyObject)settingsPanel.Content), "Portal Clips is on the Settings page");
                typeof(ClipWindow).GetMethod("SavePortalFolder", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [portalRoot]);
                Require(portalBox.Text == portalRoot && Settings.Load().PortalFolder == portalRoot
                    && Settings.Load().Folder == savedFolder && pendingBar.Visibility == Wpf.Visibility.Collapsed,
                    "Linking a Portal Clips folder saves immediately without a capture settings change");
                var clearPortal = (C.Button)window.FindName("ClearPortalButton");
                clearPortal.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(portalBox.Text == "" && Settings.Load().PortalFolder == "" && !clearPortal.IsEnabled,
                    "Clearing Portal Clips removes the saved link immediately");
                typeof(ClipWindow).GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Require(((C.TextBlock)window.FindName("ReplayLengthLabel")).Text == "Replay Length: 0:30" && window.FindName("ReplayBadge") == null, "Sidebar shows the applied replay length and Capture has no replay badge");
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
                try
                {
                    Require(!window.SaveShortcut(3, 117) && Settings.Load().Key == 116, "A conflicting shortcut leaves the saved and active shortcut intact");
                    Require(!window.SaveLibraryShortcut(3, 117) && Settings.Load().LibraryKey == 121, "A conflicting Your Clips shortcut preserves its previous binding");
                }
                finally { Win32.UnregisterHotKey(windowHandle, 99); }
                Require(window.SaveLibraryShortcut(3, 120) && Settings.Load().LibraryKey == 120 && Settings.Load().Key == 116, "Your Clips shortcut saves immediately without changing Capture");
                Require(pendingBar.Visibility == Wpf.Visibility.Collapsed, "Immediately saved shortcuts do not show the Apply bar");
                Require(!window.SaveLibraryShortcut(3, 116) && !window.SaveShortcut(3, 120) && Settings.Load().LibraryKey == 120 && Settings.Load().Key == 116, "Capture and Your Clips reject each other's shortcut without losing either binding");
                Require(!Win32.RegisterHotKey(windowHandle, 98, 3 | 0x4000, 120), "Your Clips shortcut is registered globally");
                Require(!window.IsVisible && !window.ShowInTaskbar, "Tray-only startup creates the hotkey window without displaying it");
                var shortcutBox = (C.TextBox)window.FindName("ShortcutBox");
                var shortcutKey = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, HwndSource.FromHwnd(windowHandle), Environment.TickCount, System.Windows.Input.Key.A) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                typeof(ClipWindow).GetMethod("Shortcut_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [shortcutBox, shortcutKey]);
                Require(((Wpf.FrameworkElement)window.FindName("LocationNotice")).Visibility == Wpf.Visibility.Visible && ((C.TextBlock)window.FindName("LocationNoticeText")).Text == "Include Ctrl or Alt in your shortcut." && ((Wpf.FrameworkElement)window.FindName("NoticeBorder")).Visibility == Wpf.Visibility.Collapsed, "Unmodified shortcut warns under Save Location instead of at the page bottom");
                typeof(ClipWindow).GetMethod("Shortcut_Blurred", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [shortcutBox, null]);
                Require(((Wpf.FrameworkElement)window.FindName("LocationNotice")).Visibility == Wpf.Visibility.Collapsed && shortcutBox.Text == Settings.Load().Shortcut.Replace("+", " + "), "Leaving shortcut entry restores its binding and clears the warning");
                window.SaveLibraryShortcut(0, 65);
                ((C.Button)window.FindName("LibraryNav")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                var savedViewLibrary = (ClipLibrary)((C.Border)window.FindName("LibraryHost")).Child;
                var savedTiles = (C.RadioButton)savedViewLibrary.FindName("TileViewButton");
                Require(savedTiles.IsChecked == true, "A new ClipIt window restores the saved Tiles preference");
                ((C.RadioButton)savedViewLibrary.FindName("ListViewButton")).IsChecked = true;
                Require(!Settings.Load().GameTileView, "Selecting List saves the choice immediately");
                savedTiles.IsChecked = true;
                Require(Settings.Load().GameTileView, "Selecting Tiles saves the choice immediately");
                ((C.Button)window.FindName("CaptureNav")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(((Wpf.FrameworkElement)window.FindName("LocationNotice")).Visibility == Wpf.Visibility.Collapsed && pendingBar.Visibility == Wpf.Visibility.Collapsed, "Switching to Your Clips and back leaves no stale shortcut warning or Apply bar");
                window.Hide(); window.ShowInTaskbar = false;
                Require(!((C.CheckBox)window.FindName("StartWindowsToggle")).IsEnabled, "Development instances cannot overwrite installed startup registration");
                Require(((Wpf.FrameworkElement)window.FindName("StartMinimizedRow")).Visibility == Wpf.Visibility.Visible, "Start minimized is visible when startup is enabled");
                var overlayToggle = (C.CheckBox)window.FindName("OverlayToggle");
                var ramToggle = (C.CheckBox)window.FindName("RamBufferToggle");
                ramToggle.IsChecked = true;
                Require(Settings.Load().BufferInMemory, "RAM buffer toggle persists immediately");
                ramToggle.IsChecked = false;
                Require(!Settings.Load().BufferInMemory, "Disk buffering can be restored without changing capture quality");
                overlayToggle.IsChecked = false;
                Require(!Settings.Load().Overlay && Settings.Load().StartMinimized, "Overlay saves immediately while preserving startup preferences");
                overlayToggle.IsChecked = true;
                Require(Settings.Load().Overlay, "Overlay can be re-enabled and persisted");
                using var editor = new ClipEditor();
                var range = (TrimRange)editor.FindName("Range");
                var trimButton = (C.Button)editor.FindName("TrimButton");
                Require(editor.FindName("SeekSlider") == null && ((C.Panel)range.Parent).Parent == editor.FindName("PlaybackOverlay")
                    && C.Grid.GetColumn((Wpf.UIElement)editor.FindName("VolumeSlider")) < C.Grid.GetColumn(trimButton)
                    && C.Grid.GetColumn(trimButton) < C.Grid.GetColumn((Wpf.UIElement)editor.FindName("FullscreenButton")), "One timeline and Save trim live inside the preview controls");
                typeof(ClipEditor).GetField("info", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(editor, new ClipInfo(10, 320, 180, 0, 0));
                range.Setup(10);
                Require(trimButton.Visibility != Wpf.Visibility.Visible, "Full-length selection hides Save trim");
                var playbackOverlay = (C.Border)editor.FindName("PlaybackOverlay");
                var playButton = (C.Button)editor.FindName("PlayButton");
                var speedBox = (C.ComboBox)editor.FindName("PlaybackSpeed");
                void LayoutPlaybackOverlay()
                {
                    playbackOverlay.Measure(new Wpf.Size(650, 180));
                    playbackOverlay.Arrange(new Wpf.Rect(0, 0, 650, playbackOverlay.DesiredSize.Height));
                    playbackOverlay.UpdateLayout();
                }
                LayoutPlaybackOverlay();
                var endHandle = range.Children.OfType<Thumb>().Last();
                double timelineWidth = range.ActualWidth;
                double initialHandleLeft = C.Canvas.GetLeft(endHandle);
                endHandle.RaiseEvent(new DragDeltaEventArgs(-12, 0));
                LayoutPlaybackOverlay();
                Require(trimButton.Visibility == Wpf.Visibility.Visible && range.ActualWidth == timelineWidth
                    && Math.Abs(C.Canvas.GetLeft(endHandle) - (initialHandleLeft - 12)) < .01,
                    "The right trim handle follows the drag as Save trim appears without resizing the timeline");
                Require(playbackOverlay.ActualHeight <= 58 && range.ActualWidth > 200
                    && playButton.ActualWidth == 62 && speedBox.ActualWidth == 82 && trimButton.ActualHeight >= 28,
                    "The compact playback overlay fits one row with full-size controls and room to trim");
                endHandle.RaiseEvent(new DragDeltaEventArgs(12, 0));
                LayoutPlaybackOverlay();
                Require(trimButton.Visibility != Wpf.Visibility.Visible && range.ActualWidth == timelineWidth
                    && Math.Abs(C.Canvas.GetLeft(endHandle) - initialHandleLeft) < .01,
                    "Restoring the full range hides Save trim without resizing the timeline");
                Require(C.Grid.GetColumn(trimButton) == 3 && C.Grid.GetColumn(speedBox) == 4, "Save trim precedes playback speed in the overlay");
                Require(((C.Button)editor.FindName("BackButton")).Width == 36 && Wpf.Automation.AutomationProperties.GetName((C.Button)editor.FindName("BackButton")) == "Back", "Editor uses a compact accessible back arrow");
                var editorMessage = (C.TextBlock)editor.FindName("EditorMessage");
                var headerStatus = (C.Grid)editor.FindName("HeaderStatus");
                (double Mixer, double Preview, double Header) MeasureStatus()
                {
                    editor.Measure(new Wpf.Size(1100, 750));
                    editor.Arrange(new Wpf.Rect(0, 0, 1100, 750));
                    editor.UpdateLayout();
                    return (((C.Border)editor.FindName("MixerCard")).ActualHeight, ((C.Grid)editor.FindName("PreviewSlot")).ActualHeight, ((C.Grid)editor.FindName("EditorHeader")).ActualHeight);
                }
                editorMessage.Text = "";
                var emptyStatusSize = MeasureStatus();
                foreach (string message in new[] { "Clip added to Favorites.", "Clip removed from Favorites.", "Preparing audio…", new string('W', 400) })
                {
                    editorMessage.Text = message;
                    Require(MeasureStatus() == emptyStatusSize, "Header status leaves video and mixer dimensions unchanged: " + message[..Math.Min(message.Length, 40)]);
                }
                ((C.Button)editor.FindName("CancelButton")).Visibility = Wpf.Visibility.Visible;
                ((C.Grid)editor.FindName("RenderProgress")).Visibility = Wpf.Visibility.Visible;
                Require(MeasureStatus() == emptyStatusSize && editorMessage.Parent == headerStatus && C.Grid.GetColumn(headerStatus) == 2, "Messages, cancel action, and save progress occupy the fixed header space before Favorites");
                ((C.Button)editor.FindName("CancelButton")).Visibility = Wpf.Visibility.Collapsed;
                ((C.Grid)editor.FindName("RenderProgress")).Visibility = Wpf.Visibility.Collapsed;
                editorMessage.Text = "";
                typeof(ClipEditor).GetField("isFavorite", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(editor, true);
                typeof(ClipEditor).GetMethod("UpdateFavoriteButton", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
                var favoriteIcon = (System.Windows.Shapes.Path)editor.FindName("FavoriteIcon");
                Require(((System.Windows.Media.SolidColorBrush)favoriteIcon.Fill).Color == System.Windows.Media.Color.FromRgb(245, 193, 76), "Selected favorite star is gold");
                typeof(ClipEditor).GetField("isFavorite", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(editor, false);
                typeof(ClipEditor).GetMethod("UpdateFavoriteButton", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
                var game = (C.CheckBox)editor.FindName("GameEnabled");
                var discord = (C.CheckBox)editor.FindName("DiscordEnabled");
                var mic = (C.CheckBox)editor.FindName("MicrophoneEnabled");
                var currentMix = typeof(ClipEditor).GetMethod("CurrentMix", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Require(game.IsChecked == true && discord.IsChecked == true && mic.IsChecked == true && (MixSettings)currentMix.Invoke(editor, null)! == new MixSettings(), "Editor audio switches start enabled with audible source gains");
                var gameVolume = (C.Slider)editor.FindName("GameVolume");
                Require(gameVolume.Value == gameVolume.Maximum / 2 && ((C.Slider)editor.FindName("VolumeSlider")).Value == .75, "Source midpoint preserves unity gain and master starts at 75 percent");
                gameVolume.Value = 2;
                Require(((MixSettings)currentMix.Invoke(editor, null)!).GameGain == 2, "Source volume can boost to twice its original gain");
                gameVolume.Value = 1;
                Require(speedBox.Items.Count == 8 && speedBox.SelectedIndex == 3, "Playback speed defaults to 1x with eight choices");
                speedBox.SelectedIndex = 7;
                Require(((C.MediaElement)editor.FindName("Player")).SpeedRatio == 2, "Speed selection updates video playback");
                speedBox.SelectedIndex = 3;
                typeof(ClipEditor).GetMethod("ShowOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
                Require(playbackOverlay.Opacity == 0 && !playbackOverlay.IsHitTestVisible, "Windowed overlay stays hidden when the mouse is outside the preview");
                var masterToggle = (C.CheckBox)editor.FindName("MasterEnabled");
                masterToggle.IsChecked = false;
                Require(game.IsChecked == false && discord.IsChecked == false && mic.IsChecked == false, "Master off switches every source off");
                masterToggle.IsChecked = true;
                Require(game.IsChecked == true && discord.IsChecked == true && mic.IsChecked == true && gameVolume.Value == 1, "Master on restores every source without changing volume levels");
                discord.IsChecked = false;
                var mix = (MixSettings)currentMix.Invoke(editor, null)!;
                Require(mix.DiscordMuted && mix.DiscordGain == 0 && mix.GameGain == 1 && mix.MicrophoneGain == 1, "Turning off Discord mutes only that source");
                discord.IsChecked = true;
                Require(((MixSettings)currentMix.Invoke(editor, null)!).DiscordGain == 1, "Turning audio back on restores the source");

                using (var testLibrary = new ClipLibrary())
                {
                    string libraryRoot = Path.Combine(data, "library-check");
                    string gameFolder = Path.Combine(libraryRoot, "rocketleague");
                    Directory.CreateDirectory(gameFolder);
                    string target = Path.Combine(gameFolder, "original-filename.mp4");
                    File.WriteAllText(target, "disposable library fixture");
                    testLibrary.SetFolder(libraryRoot);
                    var list = (C.ListBox)testLibrary.FindName("ClipsList");
                    for (int i = 0; i < 40 && list.Items.Count == 0; i++) await Task.Delay(50);
                    Require(list.Items.Count == 1 && (string?)list.Items[0].GetType().GetProperty("Application")?.GetValue(list.Items[0]) == "Rocket League", "Existing rocketleague folders display as Rocket League");
                    testLibrary.SetFolder(libraryRoot, portalRoot);
                    for (int i = 0; i < 40 && list.Items.Count != 2; i++) await Task.Delay(50);
                    Require(list.Items.Count == 2, "Your Clips lists local and linked clips together");
                    ((C.RadioButton)testLibrary.FindName("ByGameTab")).IsChecked = true;
                    var games = (C.ListBox)testLibrary.FindName("GamesList");
                    Require(games.Items.Count == 2 && (string?)games.Items[0].GetType().GetProperty("Name")?.GetValue(games.Items[0]) == "myGAME",
                        "By Game uses the exact top-level folder name for nested linked clips and sorts it A–Z");
                    Require(testLibrary.ViewedFolder == libraryRoot, "Open folder uses the ClipIt folder at the By Game overview");
                    var openGame = typeof(ClipLibrary).GetMethod("OpenGame", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    openGame.Invoke(testLibrary, ["Rocket League"]);
                    Require(testLibrary.ViewedFolder == gameFolder, "Open folder uses the physical folder for a local game with a display name");
                    openGame.Invoke(testLibrary, ["myGAME"]);
                    Require(testLibrary.ViewedFolder == Path.Combine(portalRoot, "myGAME"),
                        "Open folder uses the linked game's top-level folder even when its clips are nested");
                    testLibrary.ShowRecent();
                    Require(testLibrary.ViewedFolder == libraryRoot, "Open folder uses the ClipIt folder in Recent after viewing a game");
                    await testLibrary.OpenClip(portalClip);
                    var portalEditor = (ClipEditor)testLibrary.FindName("Editor");
                    var portalFavorite = (C.Button)portalEditor.FindName("FavoriteButton");
                    Require(((C.TextBlock)portalEditor.FindName("ClipTitle")).Text == "Overtime"
                        && !((C.Button)portalEditor.FindName("RenameButton")).IsEnabled
                        && portalFavorite.IsEnabled,
                        "Linked clips allow favorites while keeping file edits disabled");
                    portalFavorite.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                    testLibrary.ShowDashboard();
                    ((C.RadioButton)testLibrary.FindName("FavoritesTab")).IsChecked = true;
                    Require(testLibrary.ViewedFolder == libraryRoot, "Open folder uses the ClipIt folder in Favorites after viewing a game");
                    for (int i = 0; i < 40 && list.Items.Count != 1; i++) await Task.Delay(50);
                    Require(list.Items.Count == 1 && File.ReadAllText(portalClip) == "disposable portal fixture"
                        && Directory.GetFiles(portalGame).Length == 1 && ClipFiles.IsFavorite(portalClip, libraryRoot),
                        "A Portal clip appears in Favorites without changing files in the linked folder");
                    await testLibrary.OpenClip(portalClip);
                    Require((string)portalFavorite.ToolTip == "Remove from Favorites", "Reopening a Portal clip restores its saved favorite status");
                    portalFavorite.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                    testLibrary.ShowRecent();
                    Require(!ClipFiles.IsFavorite(portalClip, libraryRoot) && File.Exists(portalClip), "Removing a Portal favorite leaves the source file in place");
                    testLibrary.ShowDashboard();
                    testLibrary.SetFolder(libraryRoot, Path.Combine(data, "unavailable"));
                    for (int i = 0; i < 40 && list.Items.Count != 1; i++) await Task.Delay(50);
                    Require(list.Items.Count == 1 && ((C.TextBlock)testLibrary.FindName("PortalMessage")).Text.Contains("unavailable"),
                        "An unavailable linked folder leaves the local library available");
                    testLibrary.SetFolder(libraryRoot);
                    await testLibrary.OpenClip(target);
                    var clipEditor = (ClipEditor)testLibrary.FindName("Editor");
                    var title = (C.TextBlock)clipEditor.FindName("ClipTitle");
                    Require(title.Text == "Rocket League", "An unrenamed clip shows the game title in the editor");
                    ((C.Button)clipEditor.FindName("RenameButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                    var nameEdit = (C.TextBox)clipEditor.FindName("NameEdit");
                    Require(nameEdit.Text == "original-filename", "The pencil opens the actual filename for editing");
                    string renamed = Path.Combine(gameFolder, "Overtime goal.mp4");
                    nameEdit.Text = "Overtime goal";
                    nameEdit.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        Wpf.PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                    for (int i = 0; i < 40 && title.Text != "Overtime goal"; i++) await Task.Delay(50);
                    Require(!File.Exists(target) && File.Exists(renamed) && title.Text == "Overtime goal", "Saving a pencil edit renames the file and replaces the game title in the editor");
                    await testLibrary.OpenClip(renamed);
                    Require(title.Text == "Overtime goal", "Reopening a clip keeps its custom title");
                    testLibrary.ShowDashboard();
                    string? ListTitle() => list.Items.Count == 1 ? (string?)list.Items[0].GetType().GetProperty("Title")?.GetValue(list.Items[0]) : null;
                    for (int i = 0; i < 40 && ListTitle() != "Overtime goal"; i++) await Task.Delay(50);
                    Require(ListTitle() == "Overtime goal", "Your Clips shows the persisted custom title after refreshing");
                    var favoritesTab = (C.RadioButton)testLibrary.FindName("FavoritesTab");
                    favoritesTab.IsChecked = true;
                    Require(list.Items.Count == 0, "Favorites starts empty with an unfavorited clip in the library");
                    await testLibrary.OpenClip(renamed);
                    var favoriteButton = (C.Button)clipEditor.FindName("FavoriteButton");
                    var editorActions = (C.StackPanel)favoriteButton.Parent;
                    Require((string)favoriteButton.ToolTip == "Mark as Favorite" && editorActions.Children[1] == clipEditor.FindName("ExplorerButton"), "Mark as Favorite sits before Open in Explorer");
                    favoriteButton.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                    Require(File.Exists(renamed) && title.Text == "Overtime goal" && (string)favoriteButton.ToolTip == "Remove from Favorites",
                        "Marking a clip keeps its file and title in place and allows removing the mark");
                    testLibrary.ShowDashboard();
                    for (int i = 0; i < 40 && ListTitle() != "Overtime goal"; i++) await Task.Delay(50);
                    Require(ListTitle() == "Overtime goal" && ClipFiles.GameName(renamed, libraryRoot) == "Rocket League", "Favorites shows the marked clip and preserves its original game");
                    ((C.RadioButton)testLibrary.FindName("ByGameTab")).IsChecked = true;
                    Require(((C.ListBox)testLibrary.FindName("GamesList")).Items.Count == 1, "The favorite still appears in its By Game folder");
                    ((C.RadioButton)testLibrary.FindName("RecentTab")).IsChecked = true;
                    Require(ListTitle() == "Overtime goal", "Recent still includes the favorited clip once");
                    favoritesTab.IsChecked = true;
                    list.SelectedIndex = 0;
                    list.ContextMenu.RaiseEvent(new Wpf.RoutedEventArgs(C.ContextMenu.OpenedEvent));
                    testLibrary.Refresh();
                    for (int i = 0; i < 40 && list.SelectedItem != null; i++) await Task.Delay(50);
                    ((C.MenuItem)list.ContextMenu.Items[1]).RaiseEvent(new Wpf.RoutedEventArgs(C.MenuItem.ClickEvent));
                    for (int i = 0; i < 40 && (File.Exists(renamed) || list.Items.Count != 0); i++) await Task.Delay(50);
                    Require(!File.Exists(renamed) && list.Items.Count == 0 && !ClipFiles.IsFavorite(renamed, libraryRoot),
                        "One Delete removes the selected clip and its favorite mark after a library refresh clears the selection");

                    string localFavorite = Path.Combine(gameFolder, Path.GetFileName(portalClip));
                    File.WriteAllText(localFavorite, "local copy");
                    ClipFiles.SetFavorite(localFavorite, true);
                    Require(!ClipFiles.IsFavorite(portalClip, libraryRoot), "Files with matching names in different folders have independent favorite marks");
                    string renamedFavorite = Path.Combine(gameFolder, "Second goal.mp4");
                    ClipFiles.Rename(localFavorite, renamedFavorite);
                    Require(ClipFiles.IsFavorite(renamedFavorite, libraryRoot) && !ClipFiles.IsFavorite(localFavorite, libraryRoot)
                        && ClipFiles.Title(renamedFavorite, "Rocket League") == "Second goal", "Renaming a favorite keeps its mark with the renamed clip");

                    string legacyFolder = Path.Combine(libraryRoot, "Favorites");
                    Directory.CreateDirectory(legacyFolder);
                    string legacyClip = Path.Combine(legacyFolder, "Legacy clip.mp4");
                    File.WriteAllText(legacyClip, "legacy favorite");
                    Require(ClipFiles.IsFavorite(legacyClip, libraryRoot), "Favorites from the previous storage layout remain visible");
                    ClipFiles.SetFavorite(legacyClip, false);
                    Require(!ClipFiles.IsFavorite(legacyClip, libraryRoot) && File.Exists(legacyClip), "An existing favorite can be unmarked in place");
                    Require(MediaTools.Gigabytes(1048576000).EndsWith(" GB") && MediaTools.Gigabytes(100).StartsWith("<"),
                        "Clip size uses GB and shows very small files as below the display threshold");

                    File.WriteAllText(Path.Combine(gameFolder, "First goal.mp4"), "tile fixture");
                    File.WriteAllText(Path.Combine(gameFolder, "Third goal.mp4"), "tile fixture");
                    var tileWindow = new Wpf.Window { Content = testLibrary, Width = 720, Height = 520, ShowInTaskbar = false };
                    try
                    {
                        tileWindow.Show();
                        ((C.RadioButton)testLibrary.FindName("ByGameTab")).IsChecked = true;
                        openGame.Invoke(testLibrary, ["Rocket League"]);
                        for (int i = 0; i < 40 && list.Items.Count != 3; i++) await Task.Delay(50);
                        var originalPanel = list.ItemsPanel;
                        var tiles = (C.RadioButton)testLibrary.FindName("TileViewButton");
                        Require(tiles.IsVisible && list.Items.Count == 3, "Game folders show the view switch and their own clips");
                        tiles.IsChecked = true;
                        await tileWindow.Dispatcher.InvokeAsync(tileWindow.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Wpf.Point TileOrigin(int index) => ((C.ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).TranslatePoint(new Wpf.Point(), list);
                        Require(TileOrigin(1).X > TileOrigin(0).X && TileOrigin(2).Y > TileOrigin(0).Y,
                            "Tile view wraps the third card onto a new row in a narrower window");
                        tileWindow.Width = 1020;
                        await tileWindow.Dispatcher.InvokeAsync(tileWindow.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Require(TileOrigin(2).X > TileOrigin(1).X && Math.Abs(TileOrigin(2).Y - TileOrigin(0).Y) < 1,
                            "Tile view adds a third column when the window widens");
                        ((C.RadioButton)testLibrary.FindName("ListViewButton")).IsChecked = true;
                        Require(list.ItemsPanel == originalPanel && list.Items.Count == 3, "List view restores the original layout without leaving the game");
                        tiles.IsChecked = true;
                        testLibrary.ShowRecent();
                        Require(tiles.IsVisible && list.ItemsPanel != originalPanel, "Recent shows the view switch and keeps the selected tile layout");
                        ((C.RadioButton)testLibrary.FindName("ListViewButton")).IsChecked = true;
                        Require(list.ItemsPanel == originalPanel, "Recent can switch back to the list");
                    }
                    finally { tileWindow.Close(); }
                }

                var previewHost = (C.Border)window.FindName("LibraryHost");
                var previousLibrary = previewHost.Child;
                previewHost.Child = editor;
                previewHost.Visibility = Wpf.Visibility.Visible;
                var fullscreenButton = (C.Button)editor.FindName("FullscreenButton");
                var checkUpdateButton = (C.Button)window.FindName("CheckUpdateButton");
                var previousCheckVisibility = checkUpdateButton.Visibility;
                checkUpdateButton.Visibility = Wpf.Visibility.Visible;
                window.Show();
                try
                {
                    void CheckBounds(Wpf.Window target, System.Drawing.Rectangle area, string description)
                    {
                        var frame = (Wpf.FrameworkElement)target.Content;
                        var topLeft = frame.PointToScreen(new Wpf.Point());
                        var bottomRight = frame.PointToScreen(new Wpf.Point(frame.ActualWidth, frame.ActualHeight));
                        Require(target.WindowState == Wpf.WindowState.Maximized
                            && Math.Abs(topLeft.X - area.Left) <= 1 && Math.Abs(topLeft.Y - area.Top) <= 1
                            && Math.Abs(bottomRight.X - area.Right) <= 1 && Math.Abs(bottomRight.Y - area.Bottom) <= 1,
                            $"{description} ({topLeft} to {bottomRight}, expected {area})");
                    }
                    foreach (var screen in Screen.AllScreens)
                    {
                        var workArea = screen.WorkingArea;
                        const uint noZOrderOrActivation = 0x0014;
                        if (!SetWindowPos(windowHandle, 0, workArea.Left + 20, workArea.Top + 20, workArea.Width / 2, workArea.Height / 2, noZOrderOrActivation))
                            throw new Exception("Could not position the window on " + screen.DeviceName);
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        window.WindowState = Wpf.WindowState.Maximized;
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        CheckBounds(window, workArea, $"Maximized ClipIt fits the work area of {screen.DeviceName}");
                        var updateTopLeft = checkUpdateButton.PointToScreen(new Wpf.Point());
                        var updateBottomRight = checkUpdateButton.PointToScreen(new Wpf.Point(checkUpdateButton.ActualWidth, checkUpdateButton.ActualHeight));
                        Require(checkUpdateButton.IsVisible && checkUpdateButton.ActualHeight > 0
                            && updateTopLeft.X >= workArea.Left && updateTopLeft.Y >= workArea.Top
                            && updateBottomRight.X <= workArea.Right && updateBottomRight.Y <= workArea.Bottom,
                            $"Check for updates is fully inside the work area of {screen.DeviceName}");
                        // Exercise the fullscreen button independently of whether Windows can play the test media.
                        typeof(ClipEditor).GetField("opened", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(editor, true);
                        fullscreenButton.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                        var preview = window.OwnedWindows.Cast<Wpf.Window>().Single(w => w.Title == "ClipIt · Fullscreen");
                        await preview.Dispatcher.InvokeAsync(preview.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        CheckBounds(preview, workArea, $"Fullscreen preview fits the work area of {screen.DeviceName}");
                        Require(ReferenceEquals(playButton.Style, playButton.FindResource(typeof(C.Button)))
                            && ReferenceEquals(fullscreenButton.Style, fullscreenButton.FindResource(typeof(C.Button)))
                            && ReferenceEquals(speedBox.Style, speedBox.FindResource(typeof(C.ComboBox)))
                            && ReferenceEquals(((C.ComboBoxItem)speedBox.Items[0]).Style, speedBox.FindResource(typeof(C.ComboBoxItem))),
                            "Fullscreen retains the app theme for Play, speed, speed options, and Fullscreen");
                        fullscreenButton.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                        window.WindowState = Wpf.WindowState.Normal;
                    }
                }
                finally
                {
                    editor.CloseMedia();
                    checkUpdateButton.Visibility = previousCheckVisibility;
                    previewHost.Child = previousLibrary;
                    previewHost.Visibility = Wpf.Visibility.Collapsed;
                    window.WindowState = Wpf.WindowState.Normal;
                    window.Hide();
                }

                string audioFixture = Path.Combine(data, "preview-test.mp4");
                var fixture = await MediaTools.Run("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=size=320x180:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "libx264", "-threads", "1", "-c:a", "aac", "-y", audioFixture], CancellationToken.None);
                Require(fixture.Exit == 0, "Generated audio playback fixture");
                using (var audioEditor = new ClipEditor())
                {
                    var openingAudio = audioEditor.Open(audioFixture, data);
                    (double Mixer, double Preview) MeasureEditor()
                    {
                        audioEditor.Measure(new Wpf.Size(1100, 750));
                        audioEditor.Arrange(new Wpf.Rect(0, 0, 1100, 750));
                        audioEditor.UpdateLayout();
                        return (((C.Border)audioEditor.FindName("MixerCard")).ActualHeight, ((C.Grid)audioEditor.FindName("PreviewSlot")).ActualHeight);
                    }
                    var loadingSize = MeasureEditor();
                    while (!openingAudio.IsCompleted)
                    {
                        Require(((C.TextBlock)audioEditor.FindName("EditorMessage")).Parent == audioEditor.FindName("HeaderStatus"), "Loading messages occupy the stable header status area");
                        await Task.WhenAny(openingAudio, Task.Delay(20));
                    }
                    await openingAudio;
                    var readySize = MeasureEditor();
                    Require(Math.Abs(loadingSize.Mixer - readySize.Mixer) < .01 && Math.Abs(loadingSize.Preview - readySize.Preview) < .01, "Audio preparation leaves mixer and video dimensions unchanged");
                    var ready = (bool[])typeof(ClipEditor).GetField("stemReady", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audioEditor)!;
                    for (int i = 0; i < 50 && !ready[0]; i++) await Task.Delay(100);
                    Require(ready[0], "Windows opens the floating-point boosted preview audio");
                    var audioPlayers = (System.Windows.Media.MediaPlayer[])typeof(ClipEditor).GetField("stems", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audioEditor)!;
                    Require(Math.Abs(audioPlayers[0].Volume - .375) < .001, "Preview compensates preamplification to preserve original midpoint loudness");
                    var originalBytes = File.ReadAllBytes(audioFixture);
                    var originalPreview = typeof(ClipEditor).GetField("audioPreview", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audioEditor);
                    ((C.Slider)audioEditor.FindName("GameVolume")).Value = 2;
                    Require(Math.Abs(audioPlayers[0].Volume - .75) < .001, "Raising a source above midpoint increases preview loudness");
                    ((C.ComboBox)audioEditor.FindName("PlaybackSpeed")).SelectedIndex = 1;
                    Require(audioPlayers.All(p => p.SpeedRatio == .5), "Playback speed updates every audio source together");
                    ((TrimRange)audioEditor.FindName("Range")).SetRange(.25, 1.5);
                    await Task.Delay(900);
                    Require(!audioEditor.Busy && ClipFiles.ReadPlayback(audioFixture)?.Mix.Game == 2 && originalBytes.SequenceEqual(File.ReadAllBytes(audioFixture)), "Volume changes persist without rewriting the video or blocking playback");
                    Require(ReferenceEquals(originalPreview, typeof(ClipEditor).GetField("audioPreview", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audioEditor)) && ready[0], "Slider adjustments keep the existing audio preview loaded");
                    Require(audioEditor.FindName("SaveMixButton") == null && ((TrimRange)audioEditor.FindName("Range")).Start == .25 && ((C.ComboBox)audioEditor.FindName("PlaybackSpeed")).SelectedIndex == 1, "Live mixing preserves pending trim and playback speed");
                    ((C.Slider)audioEditor.FindName("VolumeSlider")).Value = .6;
                    audioEditor.CloseMedia();
                    await audioEditor.Open(audioFixture, data);
                    Require(((C.Slider)audioEditor.FindName("GameVolume")).Value == 2 && ((C.Slider)audioEditor.FindName("VolumeSlider")).Value == .6, "Reopening restores automatically saved source and master volumes");
                    ((C.CheckBox)audioEditor.FindName("MasterEnabled")).IsChecked = false;
                    Require(audioPlayers.All(p => p.Volume == 0) && ClipFiles.ReadPlayback(audioFixture)?.MasterEnabled == false, "Master mute silences all preview players and saves its state");
                    audioEditor.CloseMedia();
                    await audioEditor.Open(audioFixture, data);
                    Require(((C.CheckBox)audioEditor.FindName("MasterEnabled")).IsChecked == false && ((C.CheckBox)audioEditor.FindName("GameEnabled")).IsChecked == false, "Master and source mute states survive reopening");
                    ((C.CheckBox)audioEditor.FindName("MasterEnabled")).IsChecked = true;
                    audioEditor.CloseMedia();
                    string renamedAudio = Path.Combine(data, "renamed-preview.mp4");
                    ClipFiles.Rename(audioFixture, renamedAudio);
                    Require(ClipFiles.ReadPlayback(renamedAudio)?.Mix.Game == 2, "Renaming a clip retains its saved playback mix");
                    ClipFiles.RemoveLabels(renamedAudio);
                    Require(ClipFiles.ReadPlayback(renamedAudio) == null, "Removing clip metadata clears saved playback settings");



                }
                if (!recordingChecks) { exit = 0; return; }
                var supervisor = (Supervisor)typeof(ClipWindow).GetField("recorder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                for (int i = 0; i < 90 && !supervisor.Recording; i++) await Task.Delay(500);
                Require(supervisor.Recording && !window.IsVisible, "Hidden startup starts recording without showing the application window");
                int libraryId = (int)typeof(ClipWindow).GetField("libraryHotkeyId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                PostMessage(windowHandle, 0x0312, libraryId, 0);
                await Task.Delay(300);
                Require(window.IsVisible && ((Wpf.FrameworkElement)window.FindName("LibraryHost")).Visibility == Wpf.Visibility.Visible && ((Wpf.FrameworkElement)window.FindName("CapturePanel")).Visibility == Wpf.Visibility.Collapsed, "Your Clips hotkey restores the hidden tray window directly to the library");
                var library = (ClipLibrary)typeof(ClipWindow).GetField("library", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Require(((C.RadioButton)library.FindName("RecentTab")).IsChecked == true, "Your Clips hotkey opens Recent for quick access to the latest saved clip");
                window.WindowState = Wpf.WindowState.Minimized;
                PostMessage(windowHandle, 0x0312, libraryId, 0); await Task.Delay(200);
                Require(window.WindowState == Wpf.WindowState.Normal, "Your Clips hotkey restores a minimized window");
                ((C.Button)window.FindName("CaptureNav")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent)); window.Hide();
                for (int i = 0; i < 90 && supervisor.Seconds < 30; i++) await Task.Delay(500);
                Require(supervisor.Seconds == 30, "Recorder heartbeat fills and caps at the selected thirty-second length");
                await Task.Delay(4000);
                string saved = "";
                supervisor.Message += message => { if (message.Type == "saved") saved = message.Path; };
                supervisor.Save();
                for (int i = 0; i < 90 && saved.Length == 0; i++) await Task.Delay(500);
                Require(File.Exists(saved), "Tray-only instance confirms a successfully captured clip");
                Require(Path.GetFileName(saved).StartsWith("__clipit_no_game__ "), "Disk replay filename begins with the selected test application's name");
                var captured = await MediaTools.Probe(saved, CancellationToken.None);
                Require(captured.Duration >= 29 && captured.Duration <= 35, "A filled thirty-second replay saves the selected duration with keyframe allowance");
                saved = ""; ramToggle.IsChecked = true;
                for (int i = 0; i < 90 && (!supervisor.Recording || supervisor.Seconds < 3); i++) await Task.Delay(500);
                Require(supervisor.Recording && Settings.Load().BufferInMemory, "Switching an active disk recorder to RAM starts a new recording worker");
                supervisor.Save();
                for (int i = 0; i < 90 && saved.Length == 0; i++) await Task.Delay(500);
                Require(File.Exists(saved) && (await MediaTools.Probe(saved, CancellationToken.None)).HasStems, "RAM mode still saves a playable clip with all four named tracks");
                Require(Path.GetFileName(saved).StartsWith("__clipit_no_game__ "), "RAM replay filename begins with the selected test application's name");
                saved = ""; ramToggle.IsChecked = false;
                for (int i = 0; i < 90 && (!supervisor.Recording || supervisor.Seconds < 3); i++) await Task.Delay(500);
                supervisor.Save();
                for (int i = 0; i < 90 && saved.Length == 0; i++) await Task.Delay(500);
                Require(File.Exists(saved) && (await MediaTools.Probe(saved, CancellationToken.None)).HasStems && !Settings.Load().BufferInMemory, "Switching back to disk continues capturing and saves all four named tracks");
                supervisor.Toggle(); ramToggle.IsChecked = true;
                Require(!supervisor.Enabled && !supervisor.Recording, "Changing storage while paused preserves the paused state");
                ramToggle.IsChecked = false; supervisor.Toggle();
                for (int i = 0; i < 90 && !supervisor.Recording; i++) await Task.Delay(500);
                Require(supervisor.Recording, "Disk recording resumes normally after a paused storage change");

                await VerifyRecording(window);
                exit = 0;
            }
            catch (Exception ex) { results.Add("FAILED: " + ex); }
            finally
            {
                overlayBackground?.Close();
                if (window != null) typeof(ClipWindow).GetMethod("Quit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                app.Shutdown();
            }

            void VerifyOverlays(ClipWindow window)
            {
                var replayCorner = (OverlayCornerPicker)window.FindName("CaptureOverlayCorner");
                var recordingCorner = (OverlayCornerPicker)window.FindName("RecordingOverlayCorner");
                var replayToggle = (C.CheckBox)window.FindName("OverlayToggle");
                var recordingToggle = (C.CheckBox)window.FindName("RecordingIndicator");
                Require(replayCorner.SelectedCorner == OverlayCorner.BottomRight && recordingCorner.SelectedCorner == OverlayCorner.BottomRight,
                    "Both overlay pickers default to bottom right");
                replayToggle.IsChecked = false;
                ((C.RadioButton)replayCorner.FindName("TopRight")).IsChecked = true;
                ((C.RadioButton)recordingCorner.FindName("TopLeft")).IsChecked = true;
                var overlaySettings = Settings.Load();
                Require(!overlaySettings.Overlay && overlaySettings.OverlayCorner == OverlayCorner.TopRight
                    && !overlaySettings.Recording.Indicator && overlaySettings.Recording.OverlayCorner == OverlayCorner.TopLeft
                    && ((Wpf.FrameworkElement)window.FindName("PendingSettingsBar")).Visibility == Wpf.Visibility.Collapsed,
                    "Overlay corners save independently even while disabled without requiring Apply");

                replayToggle.IsChecked = true;
                recordingToggle.IsChecked = true;
                var replayOverlay = (RecordingOverlay)typeof(ClipWindow).GetField("recordingOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var otherOverlay = (RecordingOverlay)typeof(ClipWindow).GetField("separateRecordingOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Require(replayOverlay.IsVisible && otherOverlay.IsVisible, "Different corners show both overlays independently");
                ((C.RadioButton)recordingCorner.FindName("TopRight")).IsChecked = true;
                Require(replayOverlay.IsVisible && !otherOverlay.IsVisible, "Overlays sharing a source and corner sit side by side");
                recordingToggle.IsChecked = false;
                ((C.RadioButton)replayCorner.FindName("BottomRight")).IsChecked = true;
                ((C.RadioButton)recordingCorner.FindName("BottomRight")).IsChecked = true;

                var overlay = new RecordingOverlay();
                try
                {
                    var area = Screen.PrimaryScreen!.WorkingArea;
                    overlay.Update(true, false, area);
                    nint hwnd = new WindowInteropHelper(overlay).Handle;
                    long styles = GetWindowLongPtr(hwnd, -20);
                    Require(overlay.IsVisible && !overlay.ShowInTaskbar && !overlay.ShowActivated && (styles & 0x080000A0) == 0x080000A0, "Overlay is visible, click-through, non-activating and absent from taskbar");
                    Require(GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == 0x11, "Overlay is excluded from Windows capture");
                    Require(GetWindowRect(hwnd, out var bottomRight), "Read bottom-right overlay bounds");
                    int horizontalInset = area.Right - bottomRight.Right;
                    int bottomInset = area.Bottom - bottomRight.Bottom;
                    foreach (var corner in Enum.GetValues<OverlayCorner>())
                    {
                        overlay.Update(true, false, area, corner);
                        bool right = corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
                        bool top = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;
                        Require(GetWindowRect(hwnd, out var rect)
                            && (right ? area.Right - rect.Right : rect.Left - area.Left) == horizontalInset
                            && (top ? rect.Top - area.Top : area.Bottom - rect.Bottom) == bottomInset
                            && horizontalInset > 0 && bottomInset > 0, $"Overlay uses matching insets at {corner}");
                    }
                    var target = new Wpf.Window { Width = 360, Height = 260, ShowInTaskbar = false, ShowActivated = false };
                    try
                    {
                        target.Show();
                        nint targetHandle = new WindowInteropHelper(target).Handle;
                        foreach (int offset in new[] { 30, 70 })
                        {
                            Require(SetWindowPos(targetHandle, 0, area.Left + offset, area.Top + offset, 360, 260, 0x0014),
                                "Move the test capture window");
                            var client = AppTargets.WindowCaptureBounds(targetHandle);
                            overlay.Update(false, false, client, OverlayCorner.TopRight, captureEnabled: true, time: "00:00:30");
                            Require(!client.IsEmpty && GetWindowRect(hwnd, out var rect)
                                && client.Right - rect.Right == horizontalInset && rect.Top - client.Top == bottomInset,
                                "The recording indicator follows the selected window's client area");
                        }
                    }
                    finally { target.Close(); }
                    overlay.Update(false, false, default);
                    Require(!overlay.IsVisible, "Turning overlay off hides its window");
                }
                finally { overlay.Close(); }
            }

            async Task VerifyRecording(ClipWindow window)
            {
                var supervisor = (Supervisor)typeof(ClipWindow).GetField("recorder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                string saved = "";
                supervisor.Message += message => { if (message.Type == "saved") saved = message.Path; };
                window.Show();
                ((C.Button)window.FindName("RecordingNav")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                var recordingAction = (C.Button)window.FindName("RecordingAction");
                var recordingPause = (C.Button)window.FindName("RecordingPause");
                var timed = (C.CheckBox)window.FindName("RecordingTimed");
                var duration = (Wpf.FrameworkElement)window.FindName("RecordingDuration");
                var useCapture = (C.CheckBox)window.FindName("RecordingUseCapture");
                var customQuality = (Wpf.FrameworkElement)window.FindName("RecordingQuality");
                Require(useCapture.IsChecked == true && customQuality.Visibility == Wpf.Visibility.Collapsed,
                    "Capture & Quality hides the recording source and quality overrides by default");
                useCapture.IsChecked = false;
                Require(customQuality.Visibility == Wpf.Visibility.Visible && !Settings.Load().Recording.UseCaptureQuality,
                    "Disabling Capture quality reveals the recording source, resolution and frame rate");
                useCapture.IsChecked = true;
                Require(timed.Content.Equals("Timed Recording") && duration.Visibility == Wpf.Visibility.Collapsed,
                    "Recording shows Timed Recording with its duration hidden by default");
                timed.IsChecked = true;
                ((C.TextBox)window.FindName("RecordingMinutes")).Text = "2";
                Require(duration.Visibility == Wpf.Visibility.Visible && Settings.Load().Recording.Timed,
                    "Enabling Timed Recording reveals and saves the duration option");

                ((C.CheckBox)window.FindName("RecordingIndicator")).IsChecked = true;
                ((C.CheckBox)window.FindName("RecordingMic")).IsChecked = true;
                ((C.Slider)window.FindName("RecordingMicBoost")).Value = 9;
                var testMic = (C.Button)window.FindName("RecordingTestMicButton");
                var testPanel = (Wpf.FrameworkElement)window.FindName("RecordingMicTestPanel");
                var testStatus = (C.TextBlock)window.FindName("RecordingMicTestStatus");
                testMic.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                window.UpdateLayout();
                Require(testPanel.IsVisible && Equals(testMic.Content, "Stop Test"), "Recording Test Mic opens the live meter");
                for (int i = 0; i < 40 && testStatus.Text == "Connecting to microphone…"; i++) await Task.Delay(100);
                Require(testStatus.Text.StartsWith("Peak ") || testStatus.Text.StartsWith("Clipping")
                    || testStatus.Text.StartsWith("No audio received"), "The microphone test reports levels or unavailable input without starting a recording");
                testMic.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(!testPanel.IsVisible && Equals(testMic.Content, "Test Mic"), "Stop Test closes the live meter");
                var liveOverlay = (RecordingOverlay)typeof(ClipWindow).GetField("recordingOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var overlayTimer = (C.TextBlock)typeof(RecordingOverlay).GetField("captureTime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(liveOverlay)!;
                var captureScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Settings.Load().Monitor) ?? Screen.PrimaryScreen!;
                overlayBackground = new Wpf.Window
                {
                    Width = 340, Height = 150, WindowStyle = Wpf.WindowStyle.None, ResizeMode = Wpf.ResizeMode.NoResize,
                    Background = System.Windows.Media.Brushes.Lime, ShowActivated = false, ShowInTaskbar = false, Topmost = true
                };
                overlayBackground.Show();
                nint backgroundHandle = new WindowInteropHelper(overlayBackground).Handle;
                nint overlayHandle = new WindowInteropHelper(liveOverlay).Handle;
                for (int i = 0; i < 20 && overlayTimer.Text != "00:02:00"; i++) await Task.Delay(100);
                Require(liveOverlay.IsVisible && overlayTimer.Text == "00:02:00", "The idle overlay previews the entered recording duration");
                await Task.Delay(500);
                Require(GetWindowRect(overlayHandle, out var idleOverlayRect)
                    && SetWindowPos(backgroundHandle, overlayHandle, idleOverlayRect.Right - 340, idleOverlayRect.Top - 40, 365, 120, 0x0010),
                    "Place the solid color fixture directly behind the visible overlay");
                for (int i = 0; i < 30 && !recordingAction.IsEnabled; i++) await Task.Delay(100);
                Require(recordingAction.IsEnabled, "Recording is available when the save drive has room");
                for (int i = 0; i < 60 && (!supervisor.Recording || supervisor.Seconds < 3); i++) await Task.Delay(250);
                Require(supervisor.Recording && supervisor.Seconds >= 3, "Instant Replay is ready before starting the recording test");
                recordingAction.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                for (int i = 0; i < 60 && !recordingPause.IsVisible; i++) await Task.Delay(250);
                Require(recordingPause.IsVisible, "Start Recording opens an independent capture session");
                await Task.Delay(3500);
                Require(TimeSpan.TryParse(overlayTimer.Text, out var remaining) && remaining.TotalSeconds is > 0 and < 120,
                    "The overlay counts down from the configured duration during recording");
                Require(GetWindowRect(overlayHandle, out var recordingOverlayRect), "Read the visible overlay position during recording");
                Require(GetWindowRect(backgroundHandle, out var backgroundRect) && backgroundRect.Left < recordingOverlayRect.Left
                    && backgroundRect.Right > recordingOverlayRect.Right && backgroundRect.Top < recordingOverlayRect.Top
                    && backgroundRect.Bottom > recordingOverlayRect.Bottom, "The overlay is positioned over the solid color fixture");
                recordingPause.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                for (int i = 0; i < 30 && !Equals(recordingPause.Content, "Resume"); i++) await Task.Delay(100);
                Require(Equals(recordingPause.Content, "Resume"), "Recording pauses from the Recording tab");
                string pausedTimer = overlayTimer.Text;
                Require(GetWindowRect(overlayHandle, out var replayOverlayRect), "Read the visible overlay position during Instant Replay");
                saved = "";
                supervisor.Save();
                for (int i = 0; i < 60 && saved.Length == 0; i++) await Task.Delay(250);
                Require(File.Exists(saved) && Equals(recordingPause.Content, "Resume"), "Instant Replay saves while a recording is paused");
                Require(pausedTimer.EndsWith(" · Paused") && overlayTimer.Text == pausedTimer, "The countdown holds while recording is paused");
                recordingAction.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                for (int i = 0; i < 60 && !Equals(recordingAction.Content, "Start Recording"); i++) await Task.Delay(250);
                string recording = Directory.GetFiles(Settings.Load().RecordingsPath, "*.mp4").Single();
                Require((await MediaTools.Probe(recording, CancellationToken.None)).HasStems, "Stopping a paused recording saves playable video with the standard audio tracks");
                Require(await ShowsBackground(recording, captureScreen, recordingOverlayRect, Path.Combine(data, "recording-pixel.rgb")),
                    "A saved recording shows the green desktop area underneath both overlay badges");
                Require(await ShowsBackground(saved, captureScreen, replayOverlayRect, Path.Combine(data, "replay-pixel.rgb")),
                    "A saved Instant Replay shows the green desktop area underneath both overlay badges");
                overlayBackground.Close();
                overlayBackground = null;

                ((C.Button)window.FindName("LibraryNav")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                var library = (ClipLibrary)typeof(ClipWindow).GetField("library", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var recordingTab = (C.RadioButton)library.FindName("RecordingsTab");
                var libraryList = (C.ListBox)library.FindName("ClipsList");
                recordingTab.IsChecked = true;
                for (int i = 0; i < 30 && libraryList.Items.Count != 1; i++) await Task.Delay(100);
                Require(libraryList.Items.Count == 1 && library.ViewedFolder == Settings.Load().RecordingsPath,
                    "Recordings appear in their own library tab and open their save folder");
                await library.OpenClip(recording);
                var recordingEditor = (ClipEditor)library.FindName("Editor");
                Require(((C.Button)recordingEditor.FindName("PlayButton")).IsEnabled,
                    "A saved recording opens in the editor with separate audio preview");
                ((C.Button)window.FindName("FavoritesNav")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(library.ShowingFavorites && ((C.TextBlock)library.FindName("LibraryHeading")).Text == "Favorites",
                    "The Favorites sidebar button opens the Favorites view");
            }
        };
        app.Run();
        File.WriteAllText(Path.Combine(root, "desktop-check-results.json"), JsonSerializer.Serialize(new { Success = exit == 0, Data = data, Checks = results }, new JsonSerializerOptions { WriteIndented = true }));
        return exit;
    }

    private static async Task<bool> ShowsBackground(string file, Screen screen, WindowRect overlay, string pixelPath)
    {
        var info = await MediaTools.Probe(file, CancellationToken.None);
        var bounds = screen.Bounds;
        int x = (overlay.Left - bounds.Left) * info.Width / bounds.Width;
        int y = (overlay.Top - bounds.Top) * info.Height / bounds.Height;
        int width = (overlay.Right - overlay.Left) * info.Width / bounds.Width;
        int height = (overlay.Bottom - overlay.Top) * info.Height / bounds.Height;
        var result = await MediaTools.Run("ffmpeg",
            ["-v", "error", "-sseof", "-1", "-i", file, "-vf", $"crop={width}:{height}:{x}:{y},scale=1:1:flags=area,format=rgb24",
                "-frames:v", "1", "-f", "rawvideo", "-y", pixelPath], CancellationToken.None);
        if (result.Exit != 0 || !File.Exists(pixelPath)) return false;
        var pixel = File.ReadAllBytes(pixelPath);
        return pixel.Length == 3 && pixel[0] < 20 && pixel[1] > 235 && pixel[2] < 20;
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
