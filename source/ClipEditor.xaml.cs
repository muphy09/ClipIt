using System.Diagnostics;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows.Threading;
using Wpf = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace ClipIt;

public partial class ClipEditor : C.UserControl, IDisposable
{
    private string path = "";
    private string libraryFolder = "";
    private string gameName = "";
    private bool isFavorite;
    private bool isLinked, isRecording;
    private ClipInfo? info;
    private ClipStamp stamp;
    private bool playing, opened, resumeAfterSeek, loadingMix;
    private int generation;
    private CancellationTokenSource? loading, rendering;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer overlayTimer = new() { Interval = TimeSpan.FromSeconds(2.2) };
    private readonly M.MediaPlayer[] stems = [new(), new(), new()];
    private readonly bool[] stemReady = new bool[3];
    private AudioPreview? audioPreview;
    private Wpf.Window? fullscreen;
    private Wpf.Point dragStart;
    private double lastStart;
    public bool Busy => rendering != null;
    public event EventHandler? BackRequested;
    public event EventHandler<string>? FileChanged;

    public ClipEditor()
    {
        ControlInput.Install();
        InitializeComponent();
        foreach (var slider in new[] { VolumeSlider, GameVolume, DiscordVolume, MicrophoneVolume })
        {
            slider.PreviewMouseLeftButtonDown += AudioSlider_Down;
            slider.PreviewMouseMove += AudioSlider_Move;
            slider.PreviewMouseLeftButtonUp += AudioSlider_Up;
        }
        for (int i = 0; i < stems.Length; i++)
        {
            int index = i;
            stems[i].MediaOpened += (_, _) => { stemReady[index] = true; stems[index].SpeedRatio = Player.SpeedRatio; stems[index].Position = Player.Position; if (playing) stems[index].Play(); ApplyVolumes(); };
            stems[i].MediaFailed += (_, e) => { stemReady[index] = false; EditorMessage.Text = "A source's preview audio is unavailable: " + e.ErrorException.Message; };
        }
        timer.Tick += (_, _) =>
        {
            if (!opened || info == null) return;
            double p = Player.Position.TotalSeconds;
            if (playing && p >= Range.End - .025) { Pause(); Seek(Range.Start); p = Range.Start; }
            PositionLabel.Text = MediaTools.Time(p); Range.SetPosition(p);
            if (playing && audioPreview != null)
                for (int i = 0; i < stems.Length; i++) if (stemReady[i] && Math.Abs(stems[i].Position.TotalSeconds - p) > .075) stems[i].Position = TimeSpan.FromSeconds(p);
        };
        overlayTimer.Tick += (_, _) => { overlayTimer.Stop(); if (fullscreen != null && playing && !PlaybackOverlay.IsMouseOver && !Range.IsAdjusting && !TrimChanged) HideOverlay(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Suspend(); };
    }

    public async Task Open(string file, string library, string? linkedGame = null, bool recording = false)
    {
        if (Busy) return;
        int current = ++generation;
        loading?.Cancel(); loading?.Dispose(); loading = new();
        ClosePlayback(); info = null; path = file; libraryFolder = library;
        isLinked = linkedGame != null;
        isRecording = recording;
        isFavorite = ClipFiles.IsFavorite(file, library);
        gameName = linkedGame ?? ClipFiles.GameName(file, library);
        UpdateFavoriteButton();
        UpdateSaveActions();
        NameEdit.Visibility = Wpf.Visibility.Collapsed; NameDisplay.Visibility = Wpf.Visibility.Visible;
        ClipTitle.Text = isLinked || recording ? Path.GetFileNameWithoutExtension(file) : ClipFiles.Title(file, gameName);
        ClipTitle.ToolTip = ClipTitle.Text;
        PlayerPlaceholder.Text = "Loading your clip…"; PlayerPlaceholder.Visibility = Wpf.Visibility.Visible;
        SetControls(false); EditorMessage.Text = "Loading your clip…";
        try
        {
            stamp = ClipStamp.Read(file);
            var read = await MediaTools.Probe(file, loading.Token);
            if (current != generation) return;
            info = read; Range.Setup(read.Duration); lastStart = 0;
            ClipDetails.Text = $"{read.Width} × {read.Height}  ·  {MediaTools.Time(read.Duration)}  ·  {MediaTools.Gigabytes(read.Size)}";
            DurationLabel.Text = MediaTools.Time(read.Duration);
            var savedPlayback = ClipFiles.ReadPlayback(file);
            var playbackMix = savedPlayback?.Mix ?? read.Mix;
            loadingMix = true;
            MasterEnabled.IsChecked = savedPlayback?.MasterEnabled ?? true;
            VolumeSlider.Value = Math.Clamp(savedPlayback?.Master ?? .75, 0, 1);
            GameVolume.Value = Math.Clamp(playbackMix.Game, 0, 2); DiscordVolume.Value = Math.Clamp(playbackMix.Discord, 0, 2); MicrophoneVolume.Value = Math.Clamp(playbackMix.Microphone, 0, 2);
            GameEnabled.IsChecked = !playbackMix.GameMuted; DiscordEnabled.IsChecked = !playbackMix.DiscordMuted; MicrophoneEnabled.IsChecked = !playbackMix.MicrophoneMuted;
            loadingMix = false;
            GameLabel.Text = read.HasStems ? recording ? "Game or system" : "Game" : "Recorded mix";
            DiscordMixer.IsEnabled = read.HasStems && !recording;
            DiscordMixer.Opacity = DiscordMixer.IsEnabled ? 1 : .35;
            MicrophoneMixer.IsEnabled = read.HasStems;
            MicrophoneMixer.Opacity = read.HasStems ? 1 : .35;
            MixerHint.Text = read.HasStems ? "Separate sources · saves automatically" : "Older mixed audio · sources cannot be isolated";
            PlaybackSpeed.SelectedIndex = 3;
            Player.Source = new Uri(file); Player.Volume = 0; Player.Play(); Player.Pause();
            if (read.AudioTracks > 0)
            {
                EditorMessage.Text = "Preparing audio…";
                var preview = await MediaTools.ExtractStems(file, loading.Token, previewBoost: true, mixed: !read.HasStems, mixedTrack: read.OriginalMixTrack ?? 0);
                if (current != generation) { preview.Dispose(); return; }
                audioPreview = preview;
                for (int i = 0; i < preview.Files.Length; i++) { stems[i].Open(new Uri(preview.Files[i])); stems[i].Pause(); }
            }
            EditorMessage.Text = "";
            if (opened) PlayerPlaceholder.Visibility = Wpf.Visibility.Collapsed;
            SetControls(true); ApplyVolumes(); UpdateSaveActions(); timer.Start(); ShowOverlay();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (current == generation) { PlayerPlaceholder.Visibility = Wpf.Visibility.Visible; PlayerPlaceholder.Text = "This clip couldn't be prepared."; EditorMessage.Text = ex.Message; FavoriteButton.IsEnabled = File.Exists(file); } }
    }

    private static void SetAudioSliderPosition(C.Slider slider, MouseEventArgs e)
    {
        slider.ApplyTemplate();
        if (slider.Template.FindName("PART_Track", slider) is System.Windows.Controls.Primitives.Track track)
        {
            double value = track.ValueFromPoint(e.GetPosition(track));
            if (double.IsFinite(value)) slider.SetCurrentValue(C.Slider.ValueProperty, Math.Clamp(value, slider.Minimum, slider.Maximum));
        }
    }
    private void AudioSlider_Down(object sender, MouseButtonEventArgs e)
    {
        var slider = (C.Slider)sender;
        slider.Focus();
        if (slider.CaptureMouse()) SetAudioSliderPosition(slider, e);
        e.Handled = true;
    }
    private void AudioSlider_Move(object sender, MouseEventArgs e)
    {
        var slider = (C.Slider)sender;
        if (!slider.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        SetAudioSliderPosition(slider, e); e.Handled = true;
    }
    private void AudioSlider_Up(object sender, MouseButtonEventArgs e)
    {
        var slider = (C.Slider)sender;
        if (!slider.IsMouseCaptured) return;
        SetAudioSliderPosition(slider, e); slider.ReleaseMouseCapture(); e.Handled = true;
    }
    private void Master_Changed(object sender, Wpf.RoutedEventArgs e)
    {
        if (loadingMix || GameEnabled == null || DiscordEnabled == null || MicrophoneEnabled == null) return;
        loadingMix = true;
        GameEnabled.IsChecked = DiscordEnabled.IsChecked = MicrophoneEnabled.IsChecked = MasterEnabled.IsChecked == true;
        loadingMix = false;
        ApplyVolumes(); SavePlaybackSettings();
    }

    private MixSettings CurrentMix() => new(GameVolume.Value, DiscordVolume.Value, MicrophoneVolume.Value, GameEnabled.IsChecked != true, DiscordEnabled.IsChecked != true, MicrophoneEnabled.IsChecked != true);
    private void ApplyVolumes()
    {
        if (info == null || GameVolume == null || loadingMix) return;
        var mix = CurrentMix(); double master = VolumeSlider.Value; bool masterEnabled = MasterEnabled.IsChecked == true;
        if (!masterEnabled) master = 0;
        Player.Volume = 0;
        stems[0].Volume = mix.GameGain * master / 2; stems[1].Volume = mix.DiscordGain * master / 2; stems[2].Volume = mix.MicrophoneGain * master / 2;
    }
    private void Mix_Changed(object sender, Wpf.RoutedEventArgs e)
    {
        if (loadingMix || info == null) return;
        ApplyVolumes(); UpdateSaveActions();
        SavePlaybackSettings();
    }
    private void SavePlaybackSettings()
    {
        if (loadingMix || info == null || rendering != null || !File.Exists(path)) return;
        try { ClipFiles.SavePlayback(path, stamp, CurrentMix(), VolumeSlider.Value, MasterEnabled.IsChecked == true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { EditorMessage.Text = "Could not save audio settings: " + ex.Message; }
    }
    private void MixVolume_Changed(object sender, Wpf.RoutedPropertyChangedEventArgs<double> e) => Mix_Changed(sender, e);
    private void Volume_Changed(object sender, Wpf.RoutedPropertyChangedEventArgs<double> e)
    { ApplyVolumes(); SavePlaybackSettings(); }
    private void Speed_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (Player == null || PlaybackSpeed == null) return;
        double speed = (PlaybackSpeed.SelectedIndex + 1) * .25;
        Player.SpeedRatio = speed;
        foreach (var stem in stems) { stem.SpeedRatio = speed; if (opened) stem.Position = Player.Position; }
    }
    private void Player_Opened(object sender, Wpf.RoutedEventArgs e)
    {
        opened = true; PlayerPlaceholder.Visibility = Wpf.Visibility.Collapsed;
        Player.Pause(); Seek(Range.Start); PlayButton.Content = "Play";
    }
    private void Player_Failed(object sender, Wpf.ExceptionRoutedEventArgs e)
    {
        opened = false; PlayerPlaceholder.Text = "Playback is unavailable for this clip."; PlayerPlaceholder.Visibility = Wpf.Visibility.Visible;
        EditorMessage.Text = "Windows couldn't play this file. Open in Explorer is still available.";
        Log.Write("Playback: " + e.ErrorException.Message);
    }
    private void Player_Ended(object sender, Wpf.RoutedEventArgs e) { Pause(); Seek(Range.Start); }
    private void Play_Click(object sender, Wpf.RoutedEventArgs e) => TogglePlay();
    private void TogglePlay()
    {
        if (!opened || Busy || !PlayButton.IsEnabled) return;
        if (playing) Pause();
        else
        {
            if (Player.Position.TotalSeconds < Range.Start || Player.Position.TotalSeconds >= Range.End - .05) Seek(Range.Start);
            StartPlayback();
        }
        ShowOverlay();
    }
    private void StartPlayback()
    {
        Player.Play(); playing = true; PlayButton.Content = "Pause";
        if (audioPreview != null) for (int i = 0; i < 3; i++) if (stemReady[i]) { stems[i].Position = Player.Position; stems[i].Play(); }
    }
    public void Pause() { Player.Pause(); foreach (var stem in stems) stem.Pause(); playing = false; PlayButton.Content = "Play"; }
    public void Suspend() { Pause(); ExitFullscreen(); }
    private void Seek(double position)
    {
        if (info == null) return;
        position = Math.Clamp(position, 0, info.Duration);
        Player.Position = TimeSpan.FromSeconds(position);
        for (int i = 0; i < 3; i++) if (stemReady[i]) stems[i].Position = Player.Position;
        Range.SetPosition(position); PositionLabel.Text = MediaTools.Time(position);
    }
    private void Range_ScrubStarted(object? sender, EventArgs e)
    {
        if (!opened || Busy) return;
        resumeAfterSeek = playing; Pause();
    }
    private void Range_ScrubEnded(object? sender, EventArgs e)
    {
        bool resume = resumeAfterSeek; resumeAfterSeek = false;
        if (resume && opened && !Busy && IsVisible && Player.Position.TotalSeconds >= Range.Start && Player.Position.TotalSeconds < Range.End)
            StartPlayback();
    }
    private void Range_Changed(object? sender, EventArgs e)
    {
        if (info == null) return;
        if (opened && !Busy) { Pause(); Seek(Math.Abs(Range.Start - lastStart) > .0001 ? Range.Start : Math.Max(Range.Start, Range.End - 1.0 / 60)); }
        lastStart = Range.Start; UpdateSaveActions(); ShowOverlay();
    }
    private void Range_Seek(object? sender, double position) { if (opened && !Busy) { Seek(position); ShowOverlay(); } }
    private bool TrimChanged => info != null && (Range.Start > .0001 || Range.Duration - Range.End > .0001);
    private void UpdateSaveActions()
    {
        // Keep the timeline width fixed as the save action appears during a drag.
        TrimButton.Visibility = isLinked ? Wpf.Visibility.Collapsed
            : TrimChanged ? Wpf.Visibility.Visible : Wpf.Visibility.Hidden;
    }
    private async void Trim_Click(object sender, Wpf.RoutedEventArgs e) { if (info != null && Range.End - Range.Start >= .1) await Render(); }
    private async Task Render()
    {
        if (Busy || isLinked || info == null) return;
        var mix = CurrentMix(); double master = VolumeSlider.Value; bool masterEnabled = MasterEnabled.IsChecked == true;
        double start = Range.Start, end = Range.End;
        var cts = new CancellationTokenSource(); rendering = cts;
        ExitFullscreen(); ClosePlayback();
        ShowOverlay(); UpdateSaveActions();
        SetControls(false); BackButton.IsEnabled = false; CancelButton.Visibility = RenderProgress.Visibility = Wpf.Visibility.Visible; TrimButton.Content = "Saving…";
        EditorMessage.Text = "Saving your trim and mix… The original stays intact until the replacement is ready.";
        bool success = false;
        try { await MediaTools.Trim(path, path, start, end, stamp, cts.Token, mix); success = true; FileChanged?.Invoke(this, path); }
        catch (OperationCanceledException) { EditorMessage.Text = "Canceled. Your original clip has been kept."; }
        catch (Exception ex) { EditorMessage.Text = ex.Message; Log.Write("Editor: " + ex); }
        finally { rendering = null; cts.Dispose(); BackButton.IsEnabled = true; CancelButton.Visibility = RenderProgress.Visibility = Wpf.Visibility.Collapsed; TrimButton.Content = "Save trim"; }
        string message = success ? "Trim and playback mix saved. Separate source tracks remain editable." : EditorMessage.Text;
        if (success) ClipFiles.SavePlayback(path, ClipStamp.Read(path), mix, master, masterEnabled);
        await Open(path, libraryFolder, recording: isRecording);
        EditorMessage.Text = message;
    }

    private void SetControls(bool enabled)
    {
        PlayButton.IsEnabled = Range.IsEnabled = DragButton.IsEnabled = FullscreenButton.IsEnabled = enabled;
        TrimButton.IsEnabled = RenameButton.IsEnabled = enabled && !isLinked;
        FavoriteButton.IsEnabled = MasterEnabled.IsEnabled = VolumeSlider.IsEnabled = PlaybackSpeed.IsEnabled = enabled;
        GameVolume.IsEnabled = GameEnabled.IsEnabled = enabled;
        MicrophoneMixer.IsEnabled = enabled && info?.HasStems == true;
        DiscordMixer.IsEnabled = MicrophoneMixer.IsEnabled && !isRecording;
    }
    private void Cancel_Click(object sender, Wpf.RoutedEventArgs e) => rendering?.Cancel();
    private void Back_Click(object sender, Wpf.RoutedEventArgs e) { if (!Busy) { CloseMedia(); BackRequested?.Invoke(this, EventArgs.Empty); } }
    private void Explorer_Click(object sender, Wpf.RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { EditorMessage.Text = ex.Message; }
    }
    private void UpdateFavoriteButton()
    {
        string label = isFavorite ? "Remove from Favorites" : "Mark as Favorite";
        FavoriteButton.ToolTip = label;
        Wpf.Automation.AutomationProperties.SetName(FavoriteButton, label);
        FavoriteIcon.Stroke = new M.SolidColorBrush(isFavorite ? M.Color.FromRgb(245, 193, 76) : M.Color.FromRgb(169, 189, 217));
        FavoriteIcon.Fill = isFavorite ? FavoriteIcon.Stroke : M.Brushes.Transparent;
    }

    private void Favorite_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (Busy || !File.Exists(path)) return;
        bool marked = !isFavorite;
        try { ClipFiles.SetFavorite(path, marked); }
        catch (Exception ex) { EditorMessage.Text = "Could not save favorite: " + ex.Message; return; }
        isFavorite = marked;
        UpdateFavoriteButton();
        EditorMessage.Text = marked ? "Clip added to Favorites." : "Clip removed from Favorites.";
        FileChanged?.Invoke(this, path);
    }
    private void Rename_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (Busy || isLinked) return;
        NameEdit.Text = Path.GetFileNameWithoutExtension(path); NameDisplay.Visibility = Wpf.Visibility.Collapsed; NameEdit.Visibility = Wpf.Visibility.Visible; NameEdit.Focus(); NameEdit.SelectAll();
    }
    private async void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelRename(); e.Handled = true; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string name = NameEdit.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.StartsWith(".clipit-", StringComparison.OrdinalIgnoreCase) || AppTargets.FolderName(name) != name)
        { EditorMessage.Text = "Enter a valid filename without slashes, reserved names or a trailing period."; return; }
        string next = Path.Combine(Path.GetDirectoryName(path)!, name + ".mp4");
        if (string.Equals(next, path, StringComparison.Ordinal)) { CancelRename(); return; }
        await MoveClip(() =>
        {
            if (!string.Equals(next, path, StringComparison.OrdinalIgnoreCase) && File.Exists(next))
                throw new IOException("A clip with that name already exists.");
            ClipFiles.Rename(path, next);
            return next;
        }, "Clip renamed.");
    }
    private async Task MoveClip(Func<string> move, string successMessage)
    {
        if (isLinked) return;
        var mix = CurrentMix();
        double start = Range.Start, end = Range.End;
        string message;
        try
        {
            if (ClipStamp.Read(path) != stamp) throw new IOException("This clip changed. Reopen it before trying again.");
            ClosePlayback();
            path = move();
            FileChanged?.Invoke(this, path);
            message = successMessage;
        }
        catch (Exception ex) { message = ex.Message; }
        int opening = generation + 1;
        await Open(path, libraryFolder, recording: isRecording);
        if (generation != opening) return;
        if (info != null)
        {
            loadingMix = true;
            GameVolume.Value = mix.Game; DiscordVolume.Value = mix.Discord; MicrophoneVolume.Value = mix.Microphone;
            GameEnabled.IsChecked = !mix.GameMuted; DiscordEnabled.IsChecked = !mix.DiscordMuted; MicrophoneEnabled.IsChecked = !mix.MicrophoneMuted;
            loadingMix = false; ApplyVolumes(); Range.SetRange(start, end); UpdateSaveActions();
        }
        EditorMessage.Text = message;
    }
    private void CancelRename() { NameEdit.Visibility = Wpf.Visibility.Collapsed; NameDisplay.Visibility = Wpf.Visibility.Visible; }
    private void Name_Blurred(object sender, KeyboardFocusChangedEventArgs e) { if (NameEdit.Visibility == Wpf.Visibility.Visible) CancelRename(); }
    private void ShowOverlay() { if (fullscreen == null && !VideoArea.IsMouseOver) { HideOverlay(); return; } PlaybackOverlay.Opacity = 1; PlaybackOverlay.IsHitTestVisible = true; overlayTimer.Stop(); overlayTimer.Start(); }
    private void HideOverlay() { PlaybackOverlay.Opacity = 0; PlaybackOverlay.IsHitTestVisible = false; }
    private void Video_Enter(object sender, MouseEventArgs e) => ShowOverlay();
    private void Video_Move(object sender, MouseEventArgs e) => ShowOverlay();
    private void Video_Leave(object sender, MouseEventArgs e) { if (fullscreen == null || (playing && !Range.IsAdjusting && !TrimChanged)) HideOverlay(); }
    private void Video_Click(object sender, MouseButtonEventArgs e) { PreviewSurface.Focus(); TogglePlay(); e.Handled = true; }
    private void Fullscreen_Click(object sender, Wpf.RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (fullscreen != null) { ExitFullscreen(); return; }
        if (!opened || Busy) return;
        fullscreen = new Wpf.Window
        {
            Title = "ClipIt · Fullscreen", Background = M.Brushes.Black, WindowStyle = Wpf.WindowStyle.None,
            ResizeMode = Wpf.ResizeMode.NoResize, ShowInTaskbar = true, Owner = Wpf.Window.GetWindow(this),
            WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner, Width = 640, Height = 360
        };
        var preview = fullscreen;
        preview.SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(preview).Handle).AddHook(Win32.FitMaximizedWindow);
        fullscreen.PreviewKeyDown += Editor_KeyDown;
        fullscreen.Closing += (_, e) => { if (fullscreen != null) { e.Cancel = true; ExitFullscreen(); } };
        PreviewSlot.Children.Remove(PreviewSurface); preview.Content = PreviewSurface;
        preview.Show(); preview.WindowState = Wpf.WindowState.Maximized; preview.Activate(); Keyboard.Focus(PreviewSurface); ShowOverlay();
    }
    private void ExitFullscreen()
    {
        if (fullscreen == null) return;
        var window = fullscreen; fullscreen = null;
        window.Content = null; PreviewSlot.Children.Add(PreviewSurface); window.Close(); ShowOverlay();
    }
    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (NameEdit.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Escape && fullscreen != null) { ExitFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Space && e.OriginalSource is not C.Button) { TogglePlay(); e.Handled = true; }
        else if (e.Key == Key.F) { ToggleFullscreen(); e.Handled = true; }
    }
    private void Drag_Down(object sender, MouseButtonEventArgs e) => dragStart = e.GetPosition(this);
    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (Busy || e.LeftButton != MouseButtonState.Pressed || !File.Exists(path)) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - dragStart.X) < Wpf.SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - dragStart.Y) < Wpf.SystemParameters.MinimumVerticalDragDistance) return;
        Pause(); Wpf.DragDrop.DoDragDrop(DragButton, new Wpf.DataObject(Wpf.DataFormats.FileDrop, new[] { path }), Wpf.DragDropEffects.Copy);
    }
    private void ClosePlayback()
    {
        resumeAfterSeek = false;
        Range.EndScrub();
        Pause(); Player.Close(); opened = false; timer.Stop();
        for (int i = 0; i < stems.Length; i++) { stems[i].Close(); stemReady[i] = false; }
        audioPreview?.Dispose(); audioPreview = null;
    }
    public void CloseMedia() { ++generation; loading?.Cancel(); ExitFullscreen(); ClosePlayback(); }
    public void Dispose() { ++generation; loading?.Cancel(); rendering?.Cancel(); overlayTimer.Stop(); CloseMedia(); }
}
