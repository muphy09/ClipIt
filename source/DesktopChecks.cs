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
    internal static int Run(string root, bool recordingChecks = true)
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
                Require(initial.LibraryModifiers == 5 && initial.LibraryKey == (uint)Keys.B, "Your Clips defaults to Alt+Shift+B for existing and new profiles");
                Require(ClipWindow.RecordingMessage(true, true, 20, 120) == "Replay buffer is building" && ClipWindow.RecordingMessage(true, false, 0, 120) == "Replay buffer is building", "Building and restarting capture show the requested buffer message");
                Require(ClipWindow.RecordingMessage(false, false, 0, 120) == "Recording has been paused.", "Paused recording shows the requested message");
                Require(ClipWindow.RecordingMessage(true, true, 120, 120) == "Recording is active. Last 2 minutes ready for capture." && ClipWindow.RecordingMessage(true, true, 30, 30) == "Recording is active. Last 30 seconds ready for capture." && ClipWindow.RecordingMessage(true, true, 90, 90) == "Recording is active. Last 1 minute 30 seconds ready for capture.", "Active status uses the selected length with correct minute and second units");
                Require(!initial.BufferInMemory, "Disk buffering is the default for new and existing profiles");
                Require(BufferDrive.Warning(BufferDriveKind.HardDisk).Contains("HDD") && BufferDrive.Warning(BufferDriveKind.HardDisk).Contains("RAM") && BufferDrive.Warning(BufferDriveKind.SolidState).Length == 0, "HDD detection warns and recommends RAM; solid-state drives do not show that warning");
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
                var settings = initial with { Folder = Path.Combine(data, "clips"), ReplaySeconds = 30, GameWindow = ":ClipItWaiting:__clipit_no_game__.exe", StartWithWindows = true, StartMinimized = true, Modifiers = 3, Key = 118, LibraryModifiers = 3, LibraryKey = 121 };
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
                Require(replayLength.Items.Count == 10 && (string)replayLength.Items[0] == "0:30" && (string)replayLength.Items[9] == "5:00" && replayLength.SelectedIndex == 0, "Replay dropdown offers every 30-second step and restores the saved selection");
                string shortEstimate = estimate.Text;
                replayLength.SelectedIndex = 9;
                Require(estimate.Text != shortEstimate && Settings.Load().ReplaySeconds == 30, "File-size estimate updates before applying the replay length");
                bitrate.Text = "50";
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().ReplaySeconds == 300 && Settings.Load().Bitrate == 50000, "Apply saves the five-minute replay length and selected bitrate");
                Require(pendingBar.Visibility == Wpf.Visibility.Collapsed, "Successful Apply hides the pending changes bar");
                bitrate.Text = "24"; replayLength.SelectedIndex = 0;
                ((C.Button)window.FindName("ApplyButton")).RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                Require(Settings.Load().ReplaySeconds == 30 && estimate.Text == shortEstimate, "Replay length and estimate return to the thirty-second configuration");
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
                Require(trimButton.Visibility == Wpf.Visibility.Collapsed, "Full-length selection hides Save trim");
                range.SetRange(1, 10);
                Require(trimButton.Visibility == Wpf.Visibility.Visible, "Moving a trim handle shows Save trim");
                range.SetRange(0, 10);
                Require(trimButton.Visibility == Wpf.Visibility.Collapsed, "Restoring the full range hides Save trim again");
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
                    testLibrary.ShowRecent();
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
                    for (int i = 0; i < 40 && list.Items.Count != 1; i++) await Task.Delay(50);
                    Require(list.Items.Count == 1 && File.ReadAllText(portalClip) == "disposable portal fixture"
                        && Directory.GetFiles(portalGame).Length == 1 && ClipFiles.IsFavorite(portalClip, libraryRoot),
                        "A Portal clip appears in Favorites without changing files in the linked folder");
                    await testLibrary.OpenClip(portalClip);
                    Require((string)portalFavorite.Content == "Remove from Favorites", "Reopening a Portal clip restores its saved favorite status");
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
                    Require((string)favoriteButton.Content == "Mark as Favorite" && editorActions.Children[1] == clipEditor.FindName("ExplorerButton"), "Mark as Favorite sits before Open in Explorer");
                    favoriteButton.RaiseEvent(new Wpf.RoutedEventArgs(C.Button.ClickEvent));
                    Require(File.Exists(renamed) && title.Text == "Overtime goal" && (string)favoriteButton.Content == "Remove from Favorites",
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
                }

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
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
