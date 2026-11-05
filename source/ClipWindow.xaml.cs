using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Forms = System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Text.Json;

namespace ClipIt;

public partial class ClipWindow : Wpf.Window
{
    private Settings settings;
    private readonly Supervisor recorder;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Forms.ToolStripMenuItem trayPause;
    private HwndSource? source;
    private nint handle;
    private uint modifiers, key;
    private int hotkeyId = 1;
    private bool registered, quitting, restartRequested, dirty, populating = true;
    private bool capturingShortcut;
    private string lastPath = "";
    private DateTime lastRecoveryNotice;
    private ClipLibrary? library;
    private readonly AppUpdates updates = new();
    private readonly RecordingOverlay recordingOverlay = new();
    private readonly CaptureChime captureChime = new();
    private int driveCheckGeneration;
    private static readonly Media.Brush Blue = Brush("#72AAFF"), Green = Brush("#55D9AB"), Amber = Brush("#F2BC66"), Gray = Brush("#7D91AF");

    public ClipWindow()
    {
        ControlInput.Install();
        InitializeComponent();
        Title = "ClipIt " + AppUpdates.Version;
        VersionLabel.Text = "ClipIt " + AppUpdates.Version;
        HeaderVersion.Text = " /  INSTANT REPLAY · " + AppUpdates.Version;
        updates.Changed += UpdateStatus;
        Dispatcher.BeginInvoke(new Action(async () => { UpdateStatus(); await updates.Check(); }));
        Height = Math.Max(MinHeight, Math.Min(Height, Wpf.SystemParameters.WorkArea.Height - 40));
        settings = Settings.Load(); modifiers = settings.Modifiers; key = settings.Key;
        if (StartupRegistration.Available)
        {
            try { settings = settings with { StartWithWindows = StartupRegistration.Enabled }; }
            catch (Exception ex) { Log.Write("Could not read Windows startup: " + ex.Message); }
        }
        StartWindowsToggle.IsChecked = settings.StartWithWindows;
        StartMinimizedToggle.IsChecked = settings.StartMinimized;
        StartMinimizedRow.Visibility = settings.StartWithWindows ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        StartWindowsToggle.IsEnabled = StartupRegistration.Available;
        StartupHint.Text = StartupRegistration.Available ? "Open ClipIt automatically when you sign in." : "Available in the installed app. Development instances stay separate.";
        OverlayToggle.IsChecked = settings.Overlay;
        RamBufferToggle.IsChecked = settings.BufferInMemory;
        StartWindowsToggle.Checked += GeneralSetting_Changed; StartWindowsToggle.Unchecked += GeneralSetting_Changed;
        StartMinimizedToggle.Checked += GeneralSetting_Changed; StartMinimizedToggle.Unchecked += GeneralSetting_Changed;
        OverlayToggle.Checked += GeneralSetting_Changed; OverlayToggle.Unchecked += GeneralSetting_Changed;
        RamBufferToggle.Checked += GeneralSetting_Changed; RamBufferToggle.Unchecked += GeneralSetting_Changed;
        recorder = new(settings);
        FolderBox.Text = settings.Folder;
        ShortcutBox.Text = settings.Shortcut.Replace("+", " + ");
        var screens = Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            string name = DisplayNames.Get(screen.DeviceName);
            MonitorBox.Items.Add(new DisplayChoice(screen.DeviceName,
                name.Length > 0 ? $"{name} · {i + 1}" : $"Display {i + 1}", screen.Bounds.Height));
        }
        MonitorBox.SelectedIndex = Math.Max(0, MonitorBox.Items.Cast<DisplayChoice>().ToList().FindIndex(s => s.Id == settings.Monitor));
        RefreshResolutions(settings.Height);
        FpsBox.Items.Add("30 FPS"); FpsBox.Items.Add("60 FPS"); FpsBox.SelectedIndex = settings.Fps == 30 ? 0 : 1;
        for (int seconds = 30; seconds <= 300; seconds += 30) ReplayLengthBox.Items.Add(ReplayTime(seconds));
        ReplayLengthBox.SelectedIndex = settings.ReplaySeconds / 30 - 1;
        ReplayBadge.Text = ReplayTime(settings.ReplaySeconds) + " REPLAY";
        BufferTime.Text = $"00:00 / {settings.ReplaySeconds / 60:00}:{settings.ReplaySeconds % 60:00}";
        BitrateBox.Text = (settings.Bitrate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        UpdateEstimatedSize();
        MicToggle.IsChecked = settings.Microphone; CursorToggle.IsChecked = settings.Cursor;
        MicrophoneBox.Items.Add(new AudioDevice(settings.MicrophoneDevice, settings.MicrophoneDevice == "default" ? "System default microphone" : "Saved microphone · checking devices…"));
        MicrophoneBox.SelectedIndex = 0;
        MicrophoneBox.IsEnabled = settings.Microphone;
        RefreshGameApps();

        FolderBox.TextChanged += (_, _) => MarkDirty();
        BitrateBox.TextChanged += (_, _) => { MarkDirty(); UpdateEstimatedSize(); };
        ReplayLengthBox.SelectionChanged += (_, _) => { MarkDirty(); UpdateEstimatedSize(); };
        MonitorBox.SelectionChanged += (_, _) => { if (populating) return; RefreshResolutions(SelectedHeight); SuggestBitrate(); MarkDirty(); };
        QualityBox.SelectionChanged += (_, _) => { SuggestBitrate(); MarkDirty(); };
        FpsBox.SelectionChanged += (_, _) => { SuggestBitrate(); MarkDirty(); };
        MicToggle.Checked += (_, _) => { MicrophoneBox.IsEnabled = true; MarkDirty(); };
        MicToggle.Unchecked += (_, _) => { MicrophoneBox.IsEnabled = false; MarkDirty(); };
        MicrophoneBox.SelectionChanged += (_, _) => MarkDirty();
        GameAppBox.SelectionChanged += (_, _) => MarkDirty();
        CursorToggle.Checked += (_, _) => MarkDirty(); CursorToggle.Unchecked += (_, _) => MarkDirty();
        populating = false;
        ApplyButton.IsEnabled = false;
        _ = UpdateBufferDriveWarning();

        var menu = new Forms.ContextMenuStrip
        {
            BackColor = System.Drawing.Color.FromArgb(17, 26, 41), ForeColor = System.Drawing.Color.FromArgb(224, 234, 249),
            Font = new System.Drawing.Font("Segoe UI", 10), ShowImageMargin = false,
            Renderer = new TrayRenderer(), Padding = new Forms.Padding(5)
        };
        menu.Items.Add("Open ClipIt", null, (_, _) => ShowSettings());
        menu.Items.Add("Save clip", null, (_, _) => recorder.Save());
        menu.Items.Add("Your clips", null, (_, _) => ShowLibrary());
        menu.Items.Add("Open clips folder", null, (_, _) => OpenFolder());
        trayPause = new Forms.ToolStripMenuItem("Pause recording", null, (_, _) => recorder.Toggle());
        menu.Items.Add(trayPause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit ClipIt", null, (_, _) => Quit());
        foreach (Forms.ToolStripItem item in menu.Items) item.Padding = new Forms.Padding(10, 6, 10, 6);
        var resource = Wpf.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ClipIt.ico"));
        using var iconStream = resource.Stream;
        var appIcon = new System.Drawing.Icon(iconStream);
        tray = new Forms.NotifyIcon { Text = "ClipIt · starting", Icon = appIcon, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowSettings();
        tray.BalloonTipClicked += (_, _) =>
        {
            if (File.Exists(lastPath)) { ShowLibrary(); _ = library!.OpenClip(lastPath); }
        };
        recorder.Message += OnRecorderMessage;
        timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle); source.AddHook(WindowMessage);
            int enabled = 1, corners = 2;
            DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
            registered = Win32.RegisterHotKey(handle, hotkeyId, settings.Modifiers | 0x4000, settings.Key);
            if (!registered) Notice($"{settings.Shortcut} is already in use. Choose a different shortcut and apply your settings.", true);
            timer.Start();
        };
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closing += OnClosing;
        Closed += (_, _) => Cleanup();
        SizeChanged += (_, _) => UpdateBuffer();
        Wpf.Application.Current.SessionEnding += (_, _) => { quitting = true; Cleanup(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) library?.PausePlayback(); };
    }

    private void Tick()
    {
        if (Program.ShowRequested?.WaitOne(0) == true) ShowSettings();
        if (restartRequested) { restartRequested = false; recorder.Restart(); }
        recorder.Tick();
        bool active = recorder.Recording;
        var color = active ? Green : recorder.Enabled ? Amber : Gray;
        LiveDot.Fill = SideDot.Fill = color;
        LiveLabel.Foreground = color;
        LiveLabel.Text = active ? "RECORDING" : recorder.Enabled ? "RECONNECTING" : "PAUSED";
        SideStatus.Text = active ? "Recording is active" : recorder.Enabled ? "Restoring capture" : "Recording paused";
        StatusTitle.Text = !recorder.Enabled ? "Take a breather." : !active ? "Getting you back in the game." : recorder.Seconds >= settings.ReplaySeconds ? $"Your last {ReplayTime(settings.ReplaySeconds)}. Ready." : "You're covered from here.";
        ReplayBadge.Text = ReplayTime(settings.ReplaySeconds) + " REPLAY";
        StatusTitle.ToolTip = recorder.Status;
        recordingOverlay.Update(settings.Overlay, active, settings.Monitor);
        SaveClipButton.IsEnabled = active && !recorder.Saving;
        SaveButtonText.Text = recorder.Saving ? "Saving…" : "Save clip";
        PauseButton.Content = recorder.Enabled ? "Pause recording" : "Resume recording";
        trayPause.Text = recorder.Enabled ? "Pause recording" : "Resume recording";
        BufferDescription.Text = active ? (recorder.Seconds >= settings.ReplaySeconds ? "Full replay buffer  ·  " + settings.Shortcut.Replace("+", " + ") : "Building your replay buffer") : recorder.Enabled ? "Waiting for capture" : "Replay buffer paused";
        BufferTime.Text = $"{recorder.Seconds / 60:00}:{recorder.Seconds % 60:00} / {settings.ReplaySeconds / 60:00}:{settings.ReplaySeconds % 60:00}";
        UpdateBuffer();
        string tooltip = active ? $"ClipIt · {recorder.Seconds}s buffered · {settings.Shortcut}" : recorder.Enabled ? "ClipIt · recovering" : "ClipIt · paused";
        tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void UpdateBuffer()
    {
        if (BufferFill.Parent is Wpf.FrameworkElement p) BufferFill.Width = p.ActualWidth * Math.Clamp((double)recorder.Seconds / settings.ReplaySeconds, 0, 1);
    }

    private static string ReplayTime(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
    private int SelectedHeight => QualityBox.SelectedItem is int height ? height : settings.Height;
    private void RefreshResolutions(int preferred)
    {
        if (MonitorBox.SelectedItem is not DisplayChoice display) return;
        bool previous = populating; populating = true;
        int[] choices = CaptureQuality.Resolutions(display.Height);
        QualityBox.Items.Clear();
        foreach (int height in choices) QualityBox.Items.Add(height);
        QualityBox.SelectedItem = choices.LastOrDefault(h => h <= preferred, choices[0]);
        populating = previous;
    }
    private void SuggestBitrate()
    {
        if (populating || FpsBox.SelectedIndex < 0) return;
        BitrateBox.Text = (CaptureQuality.RecommendedBitrate(SelectedHeight, FpsBox.SelectedIndex == 0 ? 30 : 60) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
    private void UpdateEstimatedSize()
    {
        if (!double.TryParse(BitrateBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double mbps) || !double.IsFinite(mbps) || mbps < 5 || mbps > 100 || ReplayLengthBox.SelectedIndex < 0)
        { EstimatedSizeLabel.Text = "Enter a bitrate from 5 to 100 Mbps to estimate file size."; return; }
        double bytes = Settings.EstimateClipBytes(mbps, (ReplayLengthBox.SelectedIndex + 1) * 30);
        EstimatedSizeLabel.Text = bytes >= 1_000_000_000 ? $"Estimated file size: ~{bytes / 1_000_000_000:0.00} GB per full clip" : $"Estimated file size: ~{bytes / 1_000_000:0} MB per full clip";
    }

    private void OnRecorderMessage(WorkerMessage message)
    {
        switch (message.Type)
        {
            case "application": GameCaptureStatus.Text = message.Text; break;
            case "microphones":
                var devices = JsonSerializer.Deserialize<List<AudioDevice>>(message.Text) ?? [];
                string selected = (MicrophoneBox.SelectedItem as AudioDevice)?.Id ?? settings.MicrophoneDevice;
                if (devices.All(d => d.Id != selected)) devices.Add(new(selected, "Unavailable microphone · saved selection"));
                bool wasPopulating = populating; populating = true;
                MicrophoneBox.Items.Clear(); foreach (var device in devices) MicrophoneBox.Items.Add(device);
                MicrophoneBox.SelectedItem = devices.First(d => d.Id == selected); populating = wasPopulating;
                break;
            case "started":
                MonitorBox.ToolTip = message.Text;
                break;
            case "saved":
                library?.Refresh();
                lastPath = message.Path;
                LastClipLabel.Text = "Last saved  ·  " + Path.GetFileName(lastPath);
                LastClipLabel.ToolTip = lastPath;
                Notice("Clip saved. " + Path.GetFileName(lastPath));
                Log.Write("Saved clip: " + lastPath);
                captureChime.Play();
                recordingOverlay.FlashSaved();
                break;
            case "error": case "notice":
                Notice(message.Text, message.Type == "error");
                tray.ShowBalloonTip(4500, "ClipIt", message.Text, Forms.ToolTipIcon.Warning);
                break;
            case "recovering":
                Notice(message.Text + " The replay buffer will refill after recovery.", true);
                if (DateTime.UtcNow - lastRecoveryNotice > TimeSpan.FromMinutes(1))
                {
                    lastRecoveryNotice = DateTime.UtcNow;
                    tray.ShowBalloonTip(5000, "ClipIt · Restoring capture", "Recording is restarting automatically. Your buffer will refill.", Forms.ToolTipIcon.Warning);
                }
                break;
        }
    }

    private void MarkDirty()
    {
        if (populating) return;
        dirty = true; ApplyButton.IsEnabled = true;
        SettingsHint.Text = "You have unsaved changes.";
        SettingsSubtext.Text = "Apply when you're ready. Your current capture continues until then.";
    }

    private void GeneralSetting_Changed(object sender, Wpf.RoutedEventArgs e)
    {
        if (populating) return;
        var previous = settings;
        bool startupChanged = (StartWindowsToggle.IsChecked == true) != settings.StartWithWindows;
        bool storageChanged = (RamBufferToggle.IsChecked == true) != settings.BufferInMemory;
        try
        {
            if (storageChanged && (recorder.Saving || library?.Busy == true)) throw new InvalidOperationException("Wait for your clip to finish saving before changing replay storage.");
            var updated = settings with { StartWithWindows = StartWindowsToggle.IsChecked == true,
                StartMinimized = StartMinimizedToggle.IsChecked == true, Overlay = OverlayToggle.IsChecked == true, BufferInMemory = RamBufferToggle.IsChecked == true };
            if (startupChanged) StartupRegistration.Apply(updated.StartWithWindows);
            try { updated.Save(); }
            catch { if (startupChanged) StartupRegistration.Apply(previous.StartWithWindows); throw; }
            settings = updated; recorder.Settings = updated;
            if (storageChanged) recorder.Restart();
            GeneralSettingsStatus.Text = storageChanged ? "Replay storage saved. A fresh buffer starts when recording is active." : "Settings saved.";
        }
        catch (Exception ex)
        {
            populating = true;
            StartWindowsToggle.IsChecked = previous.StartWithWindows;
            StartMinimizedToggle.IsChecked = previous.StartMinimized;
            OverlayToggle.IsChecked = previous.Overlay;
            RamBufferToggle.IsChecked = previous.BufferInMemory;
            populating = false;
            GeneralSettingsStatus.Text = "Could not save settings: " + ex.Message;
        }
        StartMinimizedRow.Visibility = settings.StartWithWindows ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        recordingOverlay.Update(settings.Overlay, recorder.Recording, settings.Monitor);
        _ = UpdateBufferDriveWarning();
    }

    private async Task UpdateBufferDriveWarning()
    {
        int generation = ++driveCheckGeneration;
        BufferDriveWarning.Visibility = Wpf.Visibility.Collapsed;
        if (settings.BufferInMemory) return;
        string folder = settings.Folder;
        var kind = await Task.Run(() => BufferDrive.Detect(folder));
        if (cleaned || generation != driveCheckGeneration) return;
        string warning = BufferDrive.Warning(kind);
        BufferDriveWarningText.Text = warning;
        BufferDriveWarning.Visibility = warning.Length > 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        if (kind == BufferDriveKind.HardDisk) Notice(warning, true);
    }

    private void Apply_Click(object sender, Wpf.RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(FolderBox.Text)) throw new ArgumentException("Choose a save folder for your clips.");
            if (!double.TryParse(BitrateBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double mbps) || !double.IsFinite(mbps) || mbps < 5 || mbps > 100)
                throw new ArgumentException("Enter a video bitrate between 5 and 100 Mbps.");
            var updated = (settings with
            {
                Folder = Path.GetFullPath(FolderBox.Text.Trim()), Monitor = ((DisplayChoice)MonitorBox.SelectedItem!).Id,
                Height = SelectedHeight, Fps = FpsBox.SelectedIndex == 0 ? 30 : 60,
                Bitrate = (int)Math.Round(mbps * 1000), Microphone = MicToggle.IsChecked == true, Cursor = CursorToggle.IsChecked == true,
                ReplaySeconds = (ReplayLengthBox.SelectedIndex + 1) * 30,
                MicrophoneDevice = (MicrophoneBox.SelectedItem as AudioDevice)?.Id ?? "default",
                GameWindow = (GameAppBox.SelectedItem as CaptureApp)?.Token ?? "auto",
                Modifiers = modifiers, Key = key
            }).Validated();
            Directory.CreateDirectory(updated.Folder);
            string probe = Path.Combine(updated.Folder, ".clipit-check-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { } File.Delete(probe);
            bool changedKey = updated.Key != settings.Key || updated.Modifiers != settings.Modifiers || !registered;
            int newId = hotkeyId == 1 ? 2 : 1;
            if (changedKey && !Win32.RegisterHotKey(handle, newId, updated.Modifiers | 0x4000, updated.Key))
                throw new InvalidOperationException("That shortcut is already in use by another app. Choose a different combination.");
            try { updated.Save(); }
            catch { if (changedKey) Win32.UnregisterHotKey(handle, newId); throw; }
            if (changedKey) { if (registered) Win32.UnregisterHotKey(handle, hotkeyId); hotkeyId = newId; registered = true; }
            settings = updated; recorder.Settings = updated; recorder.Restart();
            _ = UpdateBufferDriveWarning();
            library?.SetFolder(updated.Folder);
            dirty = false; ApplyButton.IsEnabled = false;
            SettingsHint.Text = "Settings saved.";
            SettingsSubtext.Text = recorder.Enabled ? "Your new capture settings are active. Refilling the buffer…" : "Your new settings will be used when you resume recording.";
            Notice("All set. Your preferences are saved.");
            populating = true; FolderBox.Text = settings.Folder; populating = false;
        }
        catch (Exception ex) { Notice(ex.Message, true); }
    }

    private void Shortcut_KeyDown(object sender, Wpf.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressed == Key.Tab) { e.Handled = false; return; }
        if (pressed == Key.Escape) { ShowShortcut(); Keyboard.ClearFocus(); return; }
        if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        if (!mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Alt)) { Notice("Include Ctrl or Alt in your save shortcut.", true); return; }
        uint enteredModifiers = (mods.HasFlag(ModifierKeys.Control) ? 2u : 0) | (mods.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (mods.HasFlag(ModifierKeys.Shift) ? 4u : 0) | (mods.HasFlag(ModifierKeys.Windows) ? 8u : 0);
        SaveShortcut(enteredModifiers, (uint)KeyInterop.VirtualKeyFromKey(pressed));
    }
    internal bool SaveShortcut(uint enteredModifiers, uint enteredKey)
    {
        int newId = hotkeyId == 1 ? 2 : 1;
        bool changed = enteredModifiers != settings.Modifiers || enteredKey != settings.Key || !registered;
        try
        {
            var updated = (settings with { Modifiers = enteredModifiers, Key = enteredKey }).Validated();
            if (changed && !Win32.RegisterHotKey(handle, newId, enteredModifiers | 0x4000, enteredKey))
                throw new InvalidOperationException("That shortcut is already in use. Your previous shortcut is still active.");
            try { updated.Save(); }
            catch { if (changed) Win32.UnregisterHotKey(handle, newId); throw; }
            if (changed) { if (registered) Win32.UnregisterHotKey(handle, hotkeyId); hotkeyId = newId; registered = true; }
            settings = updated; recorder.Settings = updated; modifiers = enteredModifiers; key = enteredKey;
            ShowShortcut();
            SettingsHint.Text = "Shortcut saved.";
            SettingsSubtext.Text = dirty ? "Your other capture changes still need Apply settings." : "Your new shortcut is active and will be kept when ClipIt reopens.";
            return true;
        }
        catch (Exception ex) { Notice(ex.Message, true); ShowShortcut(); return false; }
    }
    private void ShowShortcut()
    {
        ShortcutBox.FontStyle = Wpf.FontStyles.Normal;
        ShortcutBox.Text = settings.Shortcut.Replace("+", " + ");
    }
    private void BeginShortcutInput()
    {
        capturingShortcut = true;
        ShortcutBox.Text = "Input keybind";
        ShortcutBox.FontStyle = Wpf.FontStyles.Italic;
    }
    private void Shortcut_Click(object sender, MouseButtonEventArgs e)
    {
        ShortcutBox.Focus(); BeginShortcutInput(); e.Handled = true;
    }
    private void Shortcut_Focused(object sender, KeyboardFocusChangedEventArgs e)
    {
        BeginShortcutInput();
        SettingsHint.Text = "Press your new shortcut.";
        SettingsSubtext.Text = "Use Ctrl or Alt with a key. A valid shortcut saves automatically.";
    }
    private void Shortcut_Blurred(object sender, KeyboardFocusChangedEventArgs e)
    {
        capturingShortcut = false;
        ShowShortcut();
        SettingsHint.Text = dirty ? "You have unsaved changes." : "Make it yours.";
        SettingsSubtext.Text = "Applying capture settings starts a fresh replay buffer.";
    }
    private void Browse_Click(object sender, Wpf.RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where ClipIt saves your clips", UseDescriptionForTitle = true, SelectedPath = FolderBox.Text };
        if (dialog.ShowDialog(new OwnerWindow(handle)) == Forms.DialogResult.OK) FolderBox.Text = dialog.SelectedPath;
    }
    private void OpenFolder_Click(object sender, Wpf.RoutedEventArgs e) => OpenFolder();
    private void Library_Click(object sender, Wpf.RoutedEventArgs e) => ShowLibrary();
    private void ShowLibrary()
    {
        ShowSettings();
        if (library == null) { library = new ClipLibrary(); LibraryHost.Child = library; }
        library.SetFolder(settings.Folder);
        library.ShowDashboard();
        CapturePanel.Visibility = Wpf.Visibility.Collapsed; LibraryHost.Visibility = Wpf.Visibility.Visible;
        GeneralSettingsPanel.Visibility = Wpf.Visibility.Collapsed;
        SettingsNav.Background = SettingsNav.BorderBrush = Media.Brushes.Transparent;
        LibraryNav.Background = Brush("#14294A"); LibraryNav.BorderBrush = Brush("#244876");
        CaptureNav.Background = Media.Brushes.Transparent; CaptureNav.BorderBrush = Media.Brushes.Transparent;
    }
    private void Capture_Click(object sender, Wpf.RoutedEventArgs e)
    {
        library?.PausePlayback(); LibraryHost.Visibility = Wpf.Visibility.Collapsed; CapturePanel.Visibility = Wpf.Visibility.Visible;
        GeneralSettingsPanel.Visibility = Wpf.Visibility.Collapsed;
        SettingsNav.Background = SettingsNav.BorderBrush = Media.Brushes.Transparent;
        CaptureNav.Background = Brush("#14294A"); CaptureNav.BorderBrush = Brush("#244876");
        LibraryNav.Background = Media.Brushes.Transparent; LibraryNav.BorderBrush = Media.Brushes.Transparent;
    }
    private void Settings_Click(object sender, Wpf.RoutedEventArgs e)
    {
        library?.PausePlayback();
        CapturePanel.Visibility = LibraryHost.Visibility = Wpf.Visibility.Collapsed;
        GeneralSettingsPanel.Visibility = Wpf.Visibility.Visible;
        SettingsNav.Background = Brush("#14294A"); SettingsNav.BorderBrush = Brush("#244876");
        CaptureNav.Background = CaptureNav.BorderBrush = LibraryNav.Background = LibraryNav.BorderBrush = Media.Brushes.Transparent;
    }
    private void RefreshMicrophones_Click(object sender, Wpf.RoutedEventArgs e) => recorder.RefreshMicrophones();
    private void RefreshApps_Click(object sender, Wpf.RoutedEventArgs e) => RefreshGameApps();
    private void RefreshGameApps()
    {
        string selected = (GameAppBox.SelectedItem as CaptureApp)?.Token ?? settings.GameWindow;
        var apps = AppTargets.List(); apps.Insert(0, CaptureApp.Auto);
        var choice = apps.FirstOrDefault(a => a.Token == selected || (selected != "auto" && a.Executable.Equals(AppTargets.Executable(selected), StringComparison.OrdinalIgnoreCase)));
        if (choice == null) { choice = AppTargets.FromToken(selected); apps.Add(choice); }
        bool previous = populating; populating = true;
        GameAppBox.Items.Clear(); foreach (var app in apps) GameAppBox.Items.Add(app);
        GameAppBox.SelectedItem = choice; populating = previous;
    }
    private void OpenFolder() => TryOpen(() => { Directory.CreateDirectory(settings.Folder); Process.Start(new ProcessStartInfo(settings.Folder) { UseShellExecute = true }); });
    private void TryOpen(Action action) { try { action(); } catch (Exception ex) { Notice(ex.Message, true); } }
    private void SaveClip_Click(object sender, Wpf.RoutedEventArgs e) => recorder.Save();
    private void Pause_Click(object sender, Wpf.RoutedEventArgs e) => recorder.Toggle();
    private void Minimize_Click(object sender, Wpf.RoutedEventArgs e) => WindowState = Wpf.WindowState.Minimized;
    private void Maximize_Click(object sender, Wpf.RoutedEventArgs e) => WindowState = WindowState == Wpf.WindowState.Maximized ? Wpf.WindowState.Normal : Wpf.WindowState.Maximized;
    private void Hide_Click(object sender, Wpf.RoutedEventArgs e) => Hide();
    private void Notice(string text, bool error = false)
    {
        NoticeText.Text = text; NoticeText.Foreground = error ? Brush("#F5CCA0") : Brush("#B5D3FC");
        NoticeBorder.Background = error ? Brush("#30251E") : Brush("#142942");
        NoticeBorder.BorderBrush = error ? Brush("#624931") : Brush("#2B4D76");
        NoticeBorder.Visibility = Wpf.Visibility.Visible;
        // Also expose feedback beside Apply so validation stays visible on short windows.
        if (error) { SettingsHint.Text = "A quick adjustment is needed."; SettingsSubtext.Text = text; }
    }
    private void ShowSettings() { ShowInTaskbar = true; Show(); if (WindowState == Wpf.WindowState.Minimized) WindowState = Wpf.WindowState.Normal; Activate(); }
    private void UpdateStatus()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(UpdateStatus); return; }
        if (cleaned) return;
        UpdateButton.Content = "- " + updates.Text;
        bool clickable = updates.CanApply || updates.CanRetry;
        UpdateButton.Visibility = clickable ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        UpdateLabel.Visibility = clickable ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        UpdateLabel.Text = "- " + updates.Text;
        UpdateButton.Foreground = updates.CanApply ? Blue : updates.CanRetry ? Amber : Brush("#7890B0");
    }
    private async void Update_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (updates.CanRetry) { await updates.Check(); return; }
        if (!updates.CanApply) return;
        if (recorder.Saving || library?.Busy == true) { Notice("Wait for your clip to finish saving, then apply the update."); return; }
        try { updates.ScheduleApply(); Quit(); }
        catch (Exception ex) { Notice("The update couldn't start: " + ex.Message, true); }
    }
    private void Quit() { quitting = true; Close(); Wpf.Application.Current.Shutdown(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!quitting) { e.Cancel = true; Hide(); } }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) restartRequested = true; }
    private void DisplayChanged(object? sender, EventArgs e) { restartRequested = true; }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && (int)wParam == hotkeyId)
        {
            if (!capturingShortcut) recorder.Save();
            handled = true;
        }
        return 0;
    }
    private bool cleaned;
    private void Cleanup()
    {
        if (cleaned) return; cleaned = true;
        updates.Changed -= UpdateStatus; updates.Dispose();
        timer.Stop(); if (registered) Win32.UnregisterHotKey(handle, hotkeyId);
        source?.RemoveHook(WindowMessage);
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        recorder.Dispose(); tray.Visible = false; var icon = tray.Icon; var menu = tray.ContextMenuStrip; tray.Dispose(); icon?.Dispose(); menu?.Dispose();
        library?.Dispose();
        recordingOverlay.Close(); captureChime.Dispose();
    }
    private static Media.Brush Brush(string hex) => (Media.Brush)new Media.BrushConverter().ConvertFromString(hex)!;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    private sealed record DisplayChoice(string Id, string Label, int Height) { public override string ToString() => Label; }
    private sealed record OwnerWindow(nint Handle) : Forms.IWin32Window;
    private sealed class TrayRenderer : Forms.ToolStripProfessionalRenderer
    {
        public TrayRenderer() : base(new TrayColors { UseSystemColors = false }) { }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var color = !e.Item.Enabled ? System.Drawing.Color.FromArgb(125, 145, 175)
                : e.Item.Selected || e.Item.Pressed ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(224, 234, 249);
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
        }
    }
    private sealed class TrayColors : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(17, 26, 41);
        public override System.Drawing.Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(31, 57, 94);
        public override System.Drawing.Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override System.Drawing.Color MenuItemPressedGradientBegin => MenuItemSelected;
        public override System.Drawing.Color MenuItemPressedGradientMiddle => MenuItemSelected;
        public override System.Drawing.Color MenuItemPressedGradientEnd => MenuItemSelected;
        public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(53, 94, 150);
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(41, 59, 85);
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(41, 59, 85);
        public override System.Drawing.Color SeparatorLight => ToolStripDropDownBackground;
    }
}
