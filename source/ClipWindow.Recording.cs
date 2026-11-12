using System.Globalization;
using Wpf = System.Windows;
using Forms = System.Windows.Forms;

namespace ClipIt;

public partial class ClipWindow
{
    private readonly RecordingSession session = new();
    private bool populatingRecording, recordingActive, recordingPaused, recordingStopping, recordingPausePending;
    private int recordingSeconds;
    private long recordingBytes;
    private long? recordingFreeSpace;
    private string? recordingDiskProblem;
    private DateTime nextRecordingDiskCheck;
    private int recordingDiskGeneration;
    private int recordingHotkeyId = 5;
    private bool recordingRegistered;
    private string recordingMonitor = "";
    private List<AudioDevice> recordingDevices = [new("default", "System default microphone")];

    private sealed record RecordingSourceChoice(string Label, string Monitor = "", string Window = "")
    {
        public override string ToString() => Label;
    }

    private sealed record RecordingResolution(int Height, string Label)
    {
        public override string ToString() => Label;
    }

    private void InitializeRecording()
    {
        var options = settings.Recording;
        populatingRecording = true;
        RefreshRecordingSources();
        RecordingHeight.ItemsSource = new RecordingResolution[] { new(32768, "Original"), new(1440, "1440p"), new(1080, "1080p"), new(720, "720p") };
        RecordingHeight.SelectedItem = RecordingHeight.Items.Cast<RecordingResolution>().FirstOrDefault(r => r.Height == options.Height);
        RecordingFps.ItemsSource = new[] { 30, 60 };
        RecordingFps.SelectedItem = options.Fps;
        RecordingUseCapture.IsChecked = options.UseCaptureQuality;
        RecordingAudio.IsChecked = options.Audio;
        RecordingMic.IsChecked = options.Microphone ?? settings.Microphone;
        RecordingCursor.IsChecked = options.Cursor ?? settings.Cursor;
        RecordingIndicator.IsChecked = options.Indicator;
        RecordingTime.IsChecked = options.ShowTime;
        RecordingSounds.IsChecked = options.Sounds;
        RecordingTimed.IsChecked = options.Timed;
        RecordingMinutes.Text = options.Minutes.ToString(CultureInfo.InvariantCulture);
        UpdateRecordingDevices(recordingDevices);
        ShowShortcut();
        populatingRecording = false;
        foreach (var toggle in new[] { RecordingUseCapture, RecordingAudio, RecordingMic, RecordingCursor,
            RecordingIndicator, RecordingTime, RecordingSounds, RecordingTimed })
        {
            toggle.Checked += RecordingOptionsChanged;
            toggle.Unchecked += RecordingOptionsChanged;
        }
        foreach (var box in new[] { RecordingSource, RecordingHeight, RecordingFps, RecordingMicDevice })
            box.SelectionChanged += RecordingOptionsChanged;
        RecordingMinutes.TextChanged += RecordingOptionsChanged;
        session.Message += OnRecordingMessage;
        UpdateRecordingControls();
    }

    private void RefreshRecordingSources()
    {
        bool wasPopulating = populatingRecording;
        populatingRecording = true;
        var options = settings.Recording;
        var choices = new List<RecordingSourceChoice> { new("Same display as Capture") };
        int index = 0;
        foreach (var screen in Forms.Screen.AllScreens)
            choices.Add(new($"Display {++index} · {screen.Bounds.Width} × {screen.Bounds.Height}", screen.DeviceName));
        foreach (var app in AppTargets.RecordingWindows())
            choices.Add(new("Window · " + app.Name, Window: app.Token));
        var selected = choices.FirstOrDefault(c => c.Monitor == options.Monitor && c.Window == options.Window);
        if (selected == null)
        {
            selected = new(options.Window.Length > 0 ? "Unavailable window · " + AppTargets.FromToken(options.Window).Name : "Unavailable display",
                options.Monitor, options.Window);
            choices.Add(selected);
        }
        RecordingSource.ItemsSource = choices;
        RecordingSource.SelectedItem = selected;
        populatingRecording = wasPopulating;
    }

    private void RefreshRecordingSources_Click(object sender, Wpf.RoutedEventArgs e) => RefreshRecordingSources();

    private void UpdateRecordingDevices(List<AudioDevice> devices)
    {
        recordingDevices = devices;
        bool wasPopulating = populatingRecording;
        populatingRecording = true;
        string id = settings.Recording.MicrophoneDevice;
        if (id.Length == 0) id = settings.MicrophoneDevice;
        var available = devices.ToList();
        if (available.All(device => device.Id != id)) available.Add(new(id, "Unavailable microphone · saved selection"));
        RecordingMicDevice.ItemsSource = available;
        RecordingMicDevice.SelectedItem = available.First(device => device.Id == id);
        populatingRecording = wasPopulating;
    }

    private bool TryRecordingMinutes(out int minutes) =>
        int.TryParse(RecordingMinutes.Text, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) && minutes > 0;

    private void RefreshRecordingCaptureDefaults()
    {
        populatingRecording = true;
        RecordingMic.IsChecked = settings.Recording.Microphone ?? settings.Microphone;
        RecordingCursor.IsChecked = settings.Recording.Cursor ?? settings.Cursor;
        UpdateRecordingDevices(recordingDevices);
        populatingRecording = false;
        nextRecordingDiskCheck = DateTime.MinValue;
        recordingFreeSpace = null;
        UpdateRecordingControls();
    }

    private void RecordingOptionsChanged(object sender, Wpf.RoutedEventArgs e)
    {
        if (populatingRecording) return;
        var options = settings.Recording;
        var selected = RecordingSource.SelectedItem as RecordingSourceChoice;
        var updated = options with
        {
            Monitor = selected?.Monitor ?? options.Monitor,
            Window = selected?.Window ?? options.Window,
            UseCaptureQuality = RecordingUseCapture.IsChecked == true,
            Height = (RecordingHeight.SelectedItem as RecordingResolution)?.Height ?? options.Height,
            Fps = (int?)RecordingFps.SelectedItem ?? options.Fps,
            Audio = RecordingAudio.IsChecked == true,
            Microphone = ReferenceEquals(sender, RecordingMic) ? RecordingMic.IsChecked : options.Microphone,
            MicrophoneDevice = ReferenceEquals(sender, RecordingMicDevice) ? (RecordingMicDevice.SelectedItem as AudioDevice)?.Id ?? "" : options.MicrophoneDevice,
            Cursor = ReferenceEquals(sender, RecordingCursor) ? RecordingCursor.IsChecked : options.Cursor,
            Indicator = RecordingIndicator.IsChecked == true,
            ShowTime = RecordingTime.IsChecked == true,
            Sounds = RecordingSounds.IsChecked == true,
            Timed = RecordingTimed.IsChecked == true,
            Minutes = TryRecordingMinutes(out int minutes) ? minutes : options.Minutes
        };
        if (updated != options) SaveRecordingSettings(settings with { Recording = updated });
        UpdateRecordingControls();
    }

    private bool SaveRecordingSettings(Settings updated)
    {
        try
        {
            updated.Save();
            settings = updated;
            recorder.Settings = updated;
            return true;
        }
        catch (Exception ex)
        {
            RecordingStatus.Text = "Could not save recording settings. " + ex.Message;
            return false;
        }
    }

    private void Recording_Click(object sender, Wpf.RoutedEventArgs e)
    {
        SelectPage(RecordingPanel, RecordingNav);
        RefreshRecordingSources();
        recorder.RefreshMicrophones();
        nextRecordingDiskCheck = DateTime.MinValue;
        TickRecording();
    }

    private void RecordingBrowse_Click(object sender, Wpf.RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder for recordings", UseDescriptionForTitle = true, SelectedPath = settings.RecordingsPath
        };
        if (dialog.ShowDialog(new OwnerWindow(handle)) == Forms.DialogResult.OK) SetRecordingFolder(dialog.SelectedPath);
    }

    private void RecordingDefaultFolder_Click(object sender, Wpf.RoutedEventArgs e) => SetRecordingFolder("");

    private void SetRecordingFolder(string path)
    {
        if (session.Busy) return;
        if (!SaveRecordingSettings(settings with { RecordingFolder = path })) return;
        recordingFreeSpace = null;
        recordingDiskProblem = null;
        nextRecordingDiskCheck = DateTime.MinValue;
        UpdateLibraryFolder();
        TickRecording();
    }

    private void RecordingOpenFolder_Click(object sender, Wpf.RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(settings.RecordingsPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(settings.RecordingsPath) { UseShellExecute = true });
        }
        catch (Exception ex) { RecordingStatus.Text = ex.Message; }
    }

    private async Task CheckRecordingDisk()
    {
        int generation = ++recordingDiskGeneration;
        string path = settings.RecordingsPath;
        long? free = null;
        string? error = null;
        try { free = await Task.Run(() => RecordingSession.FreeSpace(path)); }
        catch (Exception ex) { error = ex.Message; }
        if (generation != recordingDiskGeneration || cleaned || path != settings.RecordingsPath) return;
        recordingFreeSpace = free;
        recordingDiskProblem = error ?? (free < RecordingSession.Reserve ? RecordingSession.DiskFull : null);
        UpdateRecordingControls();
    }

    private void TickRecording()
    {
        session.Tick();
        if (RecordingPanel.IsVisible || session.Busy)
        {
            if (DateTime.UtcNow >= nextRecordingDiskCheck)
            {
                nextRecordingDiskCheck = DateTime.UtcNow.AddSeconds(2);
                _ = CheckRecordingDisk();
            }
        }
        var options = settings.Recording;
        string monitor = recordingActive ? recordingMonitor : options.SourceMonitor(settings.Monitor);
        int seconds = options.Timed ? Math.Max(0, options.DurationSeconds - recordingSeconds) : recordingSeconds;
        recordingOverlay.Update(settings.Overlay, recorder.Recording, settings.Monitor,
            options.Indicator, recordingActive, recordingPaused, options.ShowTime ? FormatRecordingTime(seconds) : null, monitor);
    }

    private static string FormatRecordingTime(int seconds) => $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";

    private void UpdateRecordingControls()
    {
        bool busy = session.Busy;
        bool timed = RecordingTimed.IsChecked == true;
        bool invalidDuration = timed && !TryRecordingMinutes(out _);
        RecordingPath.Text = settings.RecordingsPath;
        RecordingDiskError.Text = recordingDiskProblem ?? "";
        RecordingDiskError.Visibility = recordingDiskProblem == null ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        RecordingStats.Text = string.Join("  ·  ", new[]
        {
            busy ? FormatRecordingTime(recordingSeconds) : "",
            busy ? MediaTools.Gigabytes(recordingBytes) : "",
            recordingFreeSpace is long free ? MediaTools.Gigabytes(free) + " free" : ""
        }.Where(s => s.Length != 0));
        RecordingAction.Content = busy ? recordingStopping ? "Saving…" : "Stop Recording" : "Start Recording";
        RecordingAction.IsEnabled = busy ? !recordingStopping : recordingFreeSpace >= RecordingSession.Reserve && !invalidDuration;
        RecordingPause.Visibility = recordingActive && !recordingStopping ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        RecordingPause.Content = recordingPaused ? "Resume" : "Pause";
        RecordingPause.IsEnabled = !recordingPausePending;
        RecordingChangeFolder.IsEnabled = !busy;
        RecordingResetFolder.Visibility = settings.RecordingFolder.Length == 0 ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        RecordingResetFolder.IsEnabled = !busy;
        RecordingConfiguration.IsEnabled = !busy;
        RecordingTimed.IsEnabled = RecordingMinutes.IsEnabled = !busy;
        RecordingQuality.Visibility = RecordingUseCapture.IsChecked == true ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        RecordingDuration.Visibility = timed ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        RecordingMinutes.ToolTip = invalidDuration ? "Enter a duration of at least one minute." : null;
        RecordingMicDevice.IsEnabled = RecordingMic.IsChecked == true;
        RecordingGameLevel.Visibility = recordingActive && RecordingAudio.IsChecked == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        RecordingMicLevel.Visibility = recordingActive && RecordingMic.IsChecked == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        RecordingTimeRow.Visibility = RecordingIndicator.IsChecked == true ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        RecordingTime.IsEnabled = RecordingIndicator.IsChecked == true;
    }

    private void RecordingAction_Click(object sender, Wpf.RoutedEventArgs e) => ToggleRecording();

    private void ToggleRecording()
    {
        if (session.Busy)
        {
            if (recordingStopping) return;
            recordingStopping = true;
            RecordingStatus.Text = "Saving recording…";
            session.Send("stop");
        }
        else
        {
            try
            {
                if (settings.Recording.Timed && !TryRecordingMinutes(out _)) throw new IOException("Enter a duration of at least one minute.");
                recordingFreeSpace = RecordingSession.FreeSpace(settings.RecordingsPath);
                recordingDiskProblem = recordingFreeSpace < RecordingSession.Reserve ? RecordingSession.DiskFull : null;
                if (recordingDiskProblem != null) throw new IOException(recordingDiskProblem);
                recordingSeconds = 0;
                recordingBytes = 0;
                recordingMonitor = settings.Recording.SourceMonitor(settings.Monitor);
                string windowToken = settings.Recording.SourceWindow;
                if (windowToken.Length != 0 && AppTargets.FindWindow(windowToken) is var window && window != 0)
                    recordingMonitor = Forms.Screen.FromHandle(window).DeviceName;
                session.Start(settings);
                RecordingStatus.Text = "Starting recording…";
            }
            catch (Exception ex)
            {
                RecordingStatus.Text = ex.Message;
                if (!RecordingPanel.IsVisible) tray.ShowBalloonTip(3500, "ClipIt Recording", ex.Message, Forms.ToolTipIcon.Warning);
            }
        }
        UpdateRecordingControls();
    }

    private void RecordingPause_Click(object sender, Wpf.RoutedEventArgs e)
    {
        recordingPausePending = true;
        session.Send("pause");
        UpdateRecordingControls();
    }

    private void OnRecordingMessage(WorkerMessage message)
    {
        switch (message.Type)
        {
            case "started":
                recordingActive = true;
                if (!recordingStopping) RecordingStatus.Text = "Recording · " + message.Text;
                if (settings.Recording.Sounds) captureChime.Play();
                break;
            case "paused": case "resumed":
                recordingPaused = message.Type == "paused";
                recordingPausePending = false;
                recordingSeconds = message.Seconds;
                if (!recordingStopping) RecordingStatus.Text = recordingPaused ? "Recording paused" : "Recording";
                break;
            case "progress":
                recordingSeconds = message.Seconds;
                recordingBytes = message.Bytes;
                RecordingGameLevel.Value = message.GameLevel;
                RecordingMicLevel.Value = message.MicLevel;
                break;
            case "finishing":
                recordingActive = false;
                recordingStopping = true;
                recordingSeconds = message.Seconds;
                RecordingStatus.Text = message.Text;
                break;
            case "saved":
                library?.Refresh();
                if (settings.Recording.Sounds) captureChime.Play();
                RecordingStatus.Text = message.Text;
                RecordingStatus.ToolTip = message.Path;
                if (!RecordingPanel.IsVisible) tray.ShowBalloonTip(3500, "ClipIt Recording", message.Text, Forms.ToolTipIcon.Info);
                break;
            case "notice": case "error":
                recordingPausePending = false;
                if (message.Type == "error") recordingActive = false;
                RecordingStatus.Text = message.Text;
                if (!RecordingPanel.IsVisible) tray.ShowBalloonTip(3500, "ClipIt Recording", message.Text, Forms.ToolTipIcon.Warning);
                break;
            case "exit":
                if (recordingActive || !recordingStopping && RecordingStatus.Text == "Starting recording…")
                    RecordingStatus.Text = "The recording worker exited unexpectedly.";
                recordingActive = recordingPaused = recordingStopping = recordingPausePending = false;
                recordingSeconds = 0;
                break;
        }
        UpdateRecordingControls();
    }
}
