using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClipIt;

internal sealed class Recorder : IDisposable
{
    private readonly Settings settings;
    private readonly Action<WorkerMessage> send;
    private nint output, video, display, gameAudio, discord, microphone;
    private readonly List<nint> audioEncoders = new();
    private CaptureApp? game;
    private string applicationFolder = "Desktop";
    private string discordExecutable = "";
    private bool initialized;
    private bool displayShowing;
    private readonly Obs.Signal savedCallback;
    private readonly Obs.Signal fileChangedCallback;
    private DiskReplayBuffer? diskBuffer;
    private int saved;
    private bool saving;
    private DateTime saveRequested;
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private int previousFrames;
    private DateTime lastFrame = DateTime.UtcNow;
    private readonly NativeLog logCallback;
    private string encoderName = "";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeLog(int level, nint format, nint args, nint context);
    [DllImport("obs.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void base_set_log_handler(NativeLog callback, nint context);
    [DllImport("msvcrt.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int _vsnprintf([Out] byte[] buffer, nuint count, nint format, nint args);

    public Recorder(Settings settings, Action<WorkerMessage> send)
    {
        this.settings = settings.Validated();
        this.send = send;
        savedCallback = (_, _) => Interlocked.Exchange(ref saved, 1);
        fileChangedCallback = (_, data) =>
        {
            try
            {
                var call = Marshal.PtrToStructure<Obs.CallData>(data);
                if (Obs.calldata_get_string(ref call, "next_file", out var path)) diskBuffer?.FileChanged(Obs.Text(path));
            }
            catch (Exception ex) { Log.Write("Replay file change: " + ex.Message); }
        };
        logCallback = (level, format, args, _) =>
        {
            try
            {
                var bytes = new byte[8192];
                int count = _vsnprintf(bytes, (nuint)(bytes.Length - 1), format, args);
                if (level <= 300) Log.Write("OBS: " + Encoding.UTF8.GetString(bytes, 0, count >= 0 ? Math.Min(count, bytes.Length - 1) : bytes.Length - 1).TrimEnd('\0', '\r', '\n'));
            }
            catch (Exception ex) { Log.Write("Native log formatting failed: " + ex.Message); }
        };
    }

    public void Start()
    {
        var runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
        var binaries = Path.Combine(runtime, "bin", "64bit");
        Win32.SetDllDirectory(binaries);
        Environment.SetEnvironmentVariable("PATH", binaries + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        Environment.CurrentDirectory = binaries;
        base_set_log_handler(logCallback, 0);
        Obs.obs_add_data_path(Path.Combine(runtime, "data", "libobs"));
        if (!Obs.obs_startup("en-US", Settings.DataDirectory, 0)) throw new InvalidOperationException("Could not initialize the OBS engine.");
        initialized = true;
        Log.Write("Engine " + Obs.Text(Obs.obs_get_version_string()));
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Monitor) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        uint width = (uint)screen.Bounds.Width, height = (uint)screen.Bounds.Height;
        double ratio = Math.Min(1.0, (double)settings.Height / height);
        uint outWidth = (uint)(width * ratio) / 2 * 2, outHeight = (uint)(height * ratio) / 2 * 2;
        var vi = new Obs.VideoInfo
        {
            Graphics = Path.Combine(binaries, "libobs-d3d11.dll"), FpsNum = (uint)settings.Fps, FpsDen = 1,
            BaseWidth = width, BaseHeight = height, Width = outWidth, Height = outHeight,
            Format = 2, Adapter = 0, Gpu = true, ColorSpace = 2, Range = 1, Scale = 2
        };
        int result = Obs.obs_reset_video(ref vi);
        if (result != 0) throw new InvalidOperationException($"Graphics initialization failed ({result}).");
        var ai = new Obs.AudioInfo { Rate = 48000, Speakers = 2 };
        if (!Obs.obs_reset_audio(ref ai)) throw new InvalidOperationException("Audio initialization failed.");
        foreach (var name in new[] { "win-capture", "win-wasapi", "obs-ffmpeg", "obs-nvenc", "obs-qsv11" })
        {
            var root = Path.Combine(runtime, "core", name);
            int load = Obs.obs_open_module(out var module, Path.Combine(root, name + ".dll").Replace('\\', '/'), Path.Combine(root, "data").Replace('\\', '/'));
            bool loaded = load == 0 && Obs.obs_init_module(module);
            Log.Write($"Module {name}: {load}, initialized={loaded}");
            if (!loaded && name is "win-capture" or "win-wasapi" or "obs-ffmpeg") throw new InvalidOperationException($"Required recording component {name} could not load.");
        }
        Obs.obs_post_load_modules();
        SendMicrophoneDevices();
        display = MakeSource("monitor_capture", "Monitor", d =>
        {
            Obs.obs_data_set_string(d, "monitor_id", screen.DeviceName);
            Obs.obs_data_set_int(d, "method", 2); // WGC handles HDR and multiple GPU adapters.
            Obs.obs_data_set_bool(d, "capture_cursor", settings.Cursor);
            Obs.obs_data_set_bool(d, "force_sdr", true);
        });
        game = settings.GameWindow != "auto" ? AppTargets.FromToken(settings.GameWindow) : AppTargets.FullscreenGame(screen.DeviceName);
        applicationFolder = AppTargets.FolderName(game?.Name ?? "Desktop");
        gameAudio = MakeSource("wasapi_process_output_capture", "Game", d => ConfigureProcess(d, game?.Token ?? ":ClipItWaiting:__clipit_no_game__.exe"));
        var discordApp = AppTargets.Discord(settings.DiscordExecutable);
        discordExecutable = discordApp?.Executable ?? settings.DiscordExecutable;
        discord = MakeSource("wasapi_process_output_capture", "Discord", d => ConfigureProcess(d, discordApp?.Token ?? ":Chrome_WidgetWin_1:" + AppTargets.Encode(discordExecutable)));
        if (settings.Microphone) microphone = MakeSource("wasapi_input_capture", "Microphone", d => Obs.obs_data_set_string(d, "device_id", string.IsNullOrEmpty(settings.MicrophoneDevice) ? "default" : settings.MicrophoneDevice));
        Obs.obs_set_output_source(0, display);
        // A headless frontend has no preview to mark sources as showing.
        Obs.obs_source_inc_showing(display);
        displayShowing = true;
        Obs.obs_source_set_audio_mixers(gameAudio, 1 | 2); // Playback mix plus isolated game stem.
        Obs.obs_source_set_audio_mixers(discord, 1 | 4);
        Obs.obs_set_output_source(1, gameAudio);
        Obs.obs_set_output_source(2, discord);
        if (microphone != 0) { Obs.obs_source_set_audio_mixers(microphone, 1 | 8); Obs.obs_set_output_source(3, microphone); }

        var encoders = new List<string>();
        for (nuint i = 0; Obs.obs_enum_encoder_types(i, out var id); i++) encoders.Add(Obs.Text(id));
        Log.Write("Available encoders: " + string.Join(", ", encoders));
        string[] preferred = ["obs_nvenc_h264_tex", "jim_nvenc", "h264_texture_amf", "h264_fallback_amf", "obs_qsv11_v2", "obs_qsv11"];
        string[] candidates = preferred.Where(encoders.Contains).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No supported hardware H.264 encoder is available. Check your graphics driver.");
        string[] trackNames = ["ClipIt Mix", "Game", "Discord", "Microphone"];
        for (int i = 0; i < trackNames.Length; i++)
        {
            int mixer = i;
            var encoder = WithData(d => { Obs.obs_data_set_int(d, "bitrate", 192); return Obs.obs_audio_encoder_create("ffmpeg_aac", trackNames[mixer], d, (nuint)mixer, 0); });
            if (encoder == 0) throw new InvalidOperationException("AAC audio encoder unavailable.");
            audioEncoders.Add(encoder); Obs.obs_encoder_set_audio(encoder, Obs.obs_get_audio());
        }
        if (!settings.BufferInMemory) diskBuffer = CreateDiskBuffer();
        output = WithData(d =>
        {
            if (diskBuffer != null)
            {
                ConfigureDiskSettings(d);
                return Obs.obs_output_create("ffmpeg_muxer", "Disk replay", d, 0);
            }
            Obs.obs_data_set_int(d, "max_time_sec", settings.ReplayBufferSeconds);
            Obs.obs_data_set_int(d, "max_size_mb", (long)Math.Ceiling(Settings.EstimateClipBytes(settings.Bitrate / 1000.0, settings.ReplayBufferSeconds) / 1048576.0 * 1.5) + 64);
            Obs.obs_data_set_string(d, "directory", settings.Folder);
            Obs.obs_data_set_string(d, "format", "ClipIt %CCYY-%MM-%DD %hh-%mm-%ss");
            Obs.obs_data_set_string(d, "extension", "mp4");
            Obs.obs_data_set_bool(d, "allow_spaces", true);
            return Obs.obs_output_create("replay_buffer", "RAM replay", d, 0);
        });
        if (output == 0) throw new InvalidOperationException("Replay buffer component unavailable.");
        if (diskBuffer != null) Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(output), "file_changed", fileChangedCallback, 0);
        for (int i = 0; i < audioEncoders.Count; i++) Obs.obs_output_set_audio_encoder(output, audioEncoders[i], (nuint)i);
        foreach (var candidate in candidates)
        {
            video = WithData(d =>
            {
                Obs.obs_data_set_string(d, "rate_control", "CBR");
                Obs.obs_data_set_int(d, "bitrate", settings.Bitrate);
                Obs.obs_data_set_int(d, "keyint_sec", 2);
                Obs.obs_data_set_string(d, "preset", "p4");
                Obs.obs_data_set_string(d, "profile", "high");
                Obs.obs_data_set_string(d, "multipass", "disabled");
                Obs.obs_data_set_bool(d, "lookahead", false);
                Obs.obs_data_set_bool(d, "psycho_aq", false);
                Obs.obs_data_set_int(d, "bf", 2);
                return Obs.obs_video_encoder_create(candidate, "Hardware video", d, 0);
            });
            if (video == 0) continue;
            Obs.obs_encoder_set_video(video, Obs.obs_get_video());
            Obs.obs_output_set_video_encoder(output, video);
            if (Obs.obs_output_start(output)) { encoderName = candidate; break; }
            Log.Write($"Encoder {candidate} failed: " + Obs.Text(Obs.obs_output_get_last_error(output)));
            Obs.obs_output_set_video_encoder(output, 0);
            Obs.obs_encoder_release(video);
            video = 0;
        }
        if (video == 0) throw new InvalidOperationException("Hardware encoder could not start. Check app.log for details.");
        if (diskBuffer == null) Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(output), "saved", savedCallback, 0);
        lifetime.Restart();
        diskBuffer?.StartClock();
        send(new("started", $"{outWidth} × {outHeight} · {settings.Fps} FPS · H.264 · 3 separate audio sources"));
        SendApplication();
    }

    private DiskReplayBuffer CreateDiskBuffer() => new(settings, message =>
    {
        if (message.Type is "saved" or "error") saving = false;
        send(message);
    });

    private void ConfigureDiskSettings(nint data)
    {
        Obs.obs_data_set_string(data, "path", diskBuffer!.InitialPath);
        Obs.obs_data_set_string(data, "directory", diskBuffer.DirectoryPath);
        Obs.obs_data_set_string(data, "format", ".clipit-segment-%CCYY-%MM-%DD-%hh-%mm-%ss");
        Obs.obs_data_set_string(data, "extension", "mp4");
        Obs.obs_data_set_bool(data, "split_file", true);
        Obs.obs_data_set_int(data, "max_time_sec", 2);
        Obs.obs_data_set_int(data, "max_size_mb", 0);
        Obs.obs_data_set_bool(data, "allow_overwrite", false);
        Obs.obs_data_set_bool(data, "allow_spaces", false);
    }

    private static void ConfigureProcess(nint data, string token)
    {
        Obs.obs_data_set_string(data, "window", token); Obs.obs_data_set_int(data, "priority", 2);
    }
    private void SendApplication() => send(new("application", game == null ? "Waiting for a full-screen game · or choose an app below" : "Game audio · " + game.Name));
    private void FollowGame()
    {
        var discordApp = AppTargets.Discord(settings.DiscordExecutable);
        if (discordApp != null && !discordApp.Executable.Equals(discordExecutable, StringComparison.OrdinalIgnoreCase))
        {
            WithData(d => { ConfigureProcess(d, discordApp.Token); Obs.obs_source_update(discord, d); return 0; });
            discordExecutable = discordApp.Executable;
        }
        if (settings.GameWindow != "auto" || saving) return;
        var targetDisplay = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Monitor) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var next = AppTargets.FullscreenGame(targetDisplay.DeviceName);
        if (next == null || next.Executable.Equals(game?.Executable, StringComparison.OrdinalIgnoreCase)) return;
        Obs.obs_output_stop(output);
        var wait = Stopwatch.StartNew();
        while (Obs.obs_output_active(output) && wait.Elapsed.TotalSeconds < 4) Thread.Sleep(20);
        if (Obs.obs_output_active(output)) throw new IOException("Could not switch game capture safely.");
        WithData(d => { ConfigureProcess(d, next.Token); Obs.obs_source_update(gameAudio, d); return 0; });
        game = next; applicationFolder = AppTargets.FolderName(next.Name);
        if (diskBuffer != null)
        {
            diskBuffer.Dispose(); diskBuffer = CreateDiskBuffer();
            WithData(d => { ConfigureDiskSettings(d); Obs.obs_output_update(output, d); return 0; });
        }
        if (!Obs.obs_output_start(output)) throw new IOException("Could not restart the replay buffer for the new game.");
        lifetime.Restart(); previousFrames = 0; lastFrame = DateTime.UtcNow;
        diskBuffer?.StartClock();
        SendApplication();
    }

    private static nint WithData(Func<nint,nint> action)
    {
        nint d = Obs.obs_data_create();
        try { return action(d); }
        finally { Obs.obs_data_release(d); }
    }

    public void SendMicrophoneDevices()
    {
        var devices = new List<AudioDevice> { new("default", "System default microphone") };
        nint props = Obs.obs_get_source_properties("wasapi_input_capture");
        if (props != 0)
        {
            try
            {
                var list = Obs.obs_properties_get(props, "device_id");
                if (list != 0)
                    for (nuint i = 0; i < Obs.obs_property_list_item_count(list); i++)
                    {
                        string id = Obs.Text(Obs.obs_property_list_item_string(list, i));
                        string name = Obs.Text(Obs.obs_property_list_item_name(list, i));
                        if (id.Length > 0 && devices.All(d => d.Id != id)) devices.Add(new(id, name));
                    }
            }
            finally { Obs.obs_properties_destroy(props); }
        }
        send(new("microphones", JsonSerializer.Serialize(devices)));
    }
    private static nint MakeSource(string id, string name, Action<nint> configure)
    {
        var source = WithData(d => { configure(d); return Obs.obs_source_create(id, name, d, 0); });
        if (source == 0) throw new InvalidOperationException($"Could not create {name} capture.");
        return source;
    }

    public void Save()
    {
        if (saving) { send(new("notice", "A clip is already saving. Try again when it finishes.")); return; }
        if (lifetime.Elapsed.TotalSeconds < 3) { send(new("notice", "The buffer is warming up. Try again in a few seconds.")); return; }
        try
        {
            string directory = Path.Combine(settings.Folder, applicationFolder);
            Directory.CreateDirectory(directory);
            if (diskBuffer == null) WithData(d => { Obs.obs_data_set_string(d, "directory", directory); Obs.obs_output_update(output, d); return 0; });
            string probe = Path.Combine(directory, ".clipit-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            var root = BufferDrive.VolumeRoot(settings.Folder);
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < Settings.EstimateClipBytes(settings.Bitrate / 1000.0, settings.ReplayBufferSeconds) + 128_000_000)
                throw new IOException("There is not enough free space to save a clip.");
            Obs.CallData cd = default;
            try
            {
                if (diskBuffer != null)
                {
                    diskBuffer.Tick();
                    diskBuffer.Save(Path.Combine(directory, $"ClipIt {DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid().ToString("N")[..6]}.mp4"));
                    if (!Obs.proc_handler_call(Obs.obs_output_get_proc_handler(output), "split_file", ref cd)) throw new IOException("The replay split request failed.");
                }
                else if (!Obs.proc_handler_call(Obs.obs_output_get_proc_handler(output), "save", ref cd)) throw new IOException("The replay save request failed.");
            }
            finally { Obs.bfree(cd.Stack); }
            saving = true; saveRequested = DateTime.UtcNow;
            send(new("saving", "Saving your replay…"));
        }
        catch (Exception ex) { send(new("error", "Could not save: " + ex.Message)); }
    }

    public void Tick()
    {
        diskBuffer?.Tick();
        FollowGame();
        if (Interlocked.Exchange(ref saved, 0) != 0)
        {
            Obs.CallData cd = default;
            try
            {
                Obs.proc_handler_call(Obs.obs_output_get_proc_handler(output), "get_last_replay", ref cd);
                Obs.calldata_get_string(ref cd, "path", out var p);
                var path = Obs.Text(p);
                if (File.Exists(path) && new FileInfo(path).Length > 0) send(new("saved", "Replay saved", Path:path));
                else send(new("error", "The replay save did not produce a usable file."));
            }
            finally { Obs.bfree(cd.Stack); saving = false; }
        }
        if (saving && DateTime.UtcNow - saveRequested > TimeSpan.FromSeconds(90))
            throw new IOException("Saving a clip stalled. Restarting the recorder; the unsaved buffer will be lost.");
        if (!Obs.obs_output_active(output)) throw new IOException("Recording stopped: " + Obs.Text(Obs.obs_output_get_last_error(output)));
        int frames = Obs.obs_output_get_total_frames(output);
        if (frames != previousFrames) { previousFrames = frames; lastFrame = DateTime.UtcNow; }
        if (DateTime.UtcNow - lastFrame > TimeSpan.FromSeconds(15)) throw new IOException("The video encoder stopped producing frames.");
        if (lifetime.Elapsed.TotalSeconds > 20 && (Obs.obs_source_get_width(display) == 0 || Obs.obs_source_get_height(display) == 0))
            throw new IOException("The selected display is unavailable.");
        send(new("heartbeat", encoderName, Math.Min(settings.ReplaySeconds, (int)lifetime.Elapsed.TotalSeconds), frames));
    }

    public void Dispose()
    {
        if (!initialized) return;
        if (output != 0)
        {
            if (diskBuffer == null) Obs.signal_handler_disconnect(Obs.obs_output_get_signal_handler(output), "saved", savedCallback, 0);
            else Obs.signal_handler_disconnect(Obs.obs_output_get_signal_handler(output), "file_changed", fileChangedCallback, 0);
            Obs.obs_output_stop(output);
            var end = Stopwatch.StartNew();
            while (Obs.obs_output_active(output) && end.Elapsed.TotalSeconds < 4) Thread.Sleep(50);
            Obs.obs_output_release(output); output = 0;
        }
        diskBuffer?.Dispose(); diskBuffer = null;
        for (uint i = 0; i < 4; i++) Obs.obs_set_output_source(i, 0);
        if (displayShowing) Obs.obs_source_dec_showing(display);
        foreach (var source in new[] { display, gameAudio, discord, microphone }) if (source != 0) Obs.obs_source_release(source);
        if (video != 0) Obs.obs_encoder_release(video);
        foreach (var encoder in audioEncoders) Obs.obs_encoder_release(encoder);
        Obs.obs_shutdown();
        initialized = false;
        GC.KeepAlive(savedCallback); GC.KeepAlive(fileChangedCallback); GC.KeepAlive(logCallback);
    }

    internal static int RunWorker(string pipeName, string settingsJson)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(15000); }
        catch { return 2; }
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen:true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen:true);
        var commands = new ConcurrentQueue<string>();
        void Send(WorkerMessage m) { lock (writer) writer.WriteLine(JsonSerializer.Serialize(m)); }
        var readTask = Task.Run(async () =>
        {
            try { while (await reader.ReadLineAsync() is { } line) commands.Enqueue(line); }
            catch { }
            commands.Enqueue("stop");
        });
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(Encoding.UTF8.GetString(Convert.FromBase64String(settingsJson))) ?? throw new InvalidDataException("Missing settings.");
            using var engine = new Recorder(s, Send);
            engine.Start();
            var tick = Stopwatch.StartNew();
            while (true)
            {
                while (commands.TryDequeue(out var cmd))
                {
                    if (cmd == "stop") return 0;
                    if (cmd == "save") engine.Save();
                    if (cmd == "devices") engine.SendMicrophoneDevices();
                }
                if (tick.ElapsedMilliseconds >= 1000) { engine.Tick(); tick.Restart(); }
                Thread.Sleep(50);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Worker failed: " + ex);
            try { Send(new("fatal", ex.Message)); } catch { }
            return 1;
        }
    }
}
