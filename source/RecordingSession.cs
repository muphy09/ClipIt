using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClipIt;

public sealed record RecordingOptions
{
    public string Monitor { get; init; } = "";
    public string Window { get; init; } = "";
    public bool UseCaptureQuality { get; init; } = true;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public bool Audio { get; init; } = true;
    public bool? Microphone { get; init; }
    public string MicrophoneDevice { get; init; } = "";
    public bool? Cursor { get; init; }
    public bool Indicator { get; init; }
    public bool ShowTime { get; init; } = true;
    public bool Sounds { get; init; } = true;
    public bool Timed { get; init; }
    public int Minutes { get; init; } = 30;
    public uint Modifiers { get; init; } = 1;
    public uint Key { get; init; } = (uint)Keys.V;
    internal string SourceWindow => UseCaptureQuality ? "" : Window;
    internal string SourceMonitor(string captureMonitor) => UseCaptureQuality || Monitor.Length == 0 ? captureMonitor : Monitor;
    internal int DurationSeconds => Minutes * 60;
}

internal sealed class RecordingSession : IDisposable
{
    internal const long Reserve = 2_000_000_000;
    internal const string DiskFull = "Disk is too full to record";
    private readonly ConcurrentQueue<WorkerMessage> messages = new();
    private Process? worker;
    internal bool Busy => worker != null;
    internal event Action<WorkerMessage>? Message;

    internal static long FreeSpace(string path)
    {
        while (!Directory.Exists(path))
            path = Path.GetDirectoryName(path) ?? throw new IOException("The recording drive is unavailable.");
        if (!GetDiskFreeSpaceEx(path, out ulong available, out _, out _))
            throw new IOException("Could not check free space on the recording drive.");
        return (long)Math.Min(available, long.MaxValue);
    }

    internal void Start(Settings settings)
    {
        if (Busy) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--recording-worker");
        start.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(settings)));
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) { messages.Enqueue(new("exit")); return; }
            try { if (JsonSerializer.Deserialize<WorkerMessage>(e.Data) is { } m) messages.Enqueue(m); }
            catch (JsonException) { Log.Write("Unexpected recording worker output."); }
        };
        try
        {
            if (!process.Start()) throw new IOException("Could not start recording.");
            process.BeginOutputReadLine();
            worker = process;
        }
        catch
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
            process.Dispose();
            throw;
        }
    }

    internal void Tick()
    {
        while (messages.TryDequeue(out var message))
        {
            if (message.Type == "exit")
            {
                try { worker?.StandardInput.Close(); } catch (IOException) { }
                worker?.Dispose();
                worker = null;
            }
            Message?.Invoke(message);
        }
    }

    internal void Send(string command)
    {
        try { worker?.StandardInput.WriteLine(command); }
        catch (IOException) { } // The worker may already be closing after an automatic stop.
    }

    public void Dispose()
    {
        Send("stop");
        try { worker?.StandardInput.Close(); } catch (IOException) { }
        worker?.Dispose();
        worker = null;
    }

    internal static int RunWorker(string settingsArgument)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Send(WorkerMessage m)
        {
            try { output.WriteLine(JsonSerializer.Serialize(m)); }
            catch (IOException) { } // ClipIt may have quit while the recording finishes.
        }
        var commands = new ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            try { while (await input.ReadLineAsync() is { } line) commands.Enqueue(line); }
            catch (IOException) { }
            finally { commands.Enqueue("stop"); }
        });

        string partial = "";
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(Convert.FromBase64String(settingsArgument))!.Validated();
            var options = settings.Recording;
            string directory = settings.RecordingsPath;
            if (FreeSpace(directory) < Reserve) throw new IOException(DiskFull);
            Directory.CreateDirectory(directory);
            if (FreeSpace(directory) < Reserve) throw new IOException(DiskFull);

            string windowToken = options.SourceWindow;
            string monitor = options.SourceMonitor(settings.Monitor);
            nint window = windowToken.Length == 0 ? 0 : AppTargets.FindWindow(windowToken);
            if (windowToken.Length != 0 && window == 0) throw new IOException("The selected window is unavailable.");
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == monitor);
            if (window == 0 && !options.UseCaptureQuality && options.Monitor.Length != 0 && screen == null)
                throw new IOException("The selected display is unavailable.");
            screen ??= Screen.PrimaryScreen ?? Screen.AllScreens[0];
            string source = window != 0 ? AppTargets.FromToken(windowToken).Name : "Display";
            string file = Path.Combine(directory, ClipFiles.SaveName(source) + ".mp4");
            partial = Path.Combine(directory, ".clipit-recording-" + Path.GetFileName(file));
            var capture = settings with
            {
                Monitor = screen.DeviceName,
                GameWindow = window != 0 ? windowToken : "auto",
                Height = options.UseCaptureQuality ? settings.Height : options.Height,
                Fps = options.UseCaptureQuality ? settings.Fps : options.Fps,
                Bitrate = options.UseCaptureQuality ? settings.Bitrate :
                    CaptureQuality.RecommendedBitrate(options.Height == 32768 ?
                        (window != 0 ? AppTargets.WindowSize(window).Height : screen.Bounds.Height)
                        : options.Height, options.Fps),
                Microphone = options.Microphone ?? settings.Microphone,
                MicrophoneDevice = options.MicrophoneDevice.Length == 0 ? settings.MicrophoneDevice : options.MicrophoneDevice,
                Cursor = options.Cursor ?? settings.Cursor
            };
            string reason;
            using (var engine = new Recorder(capture, m => { if (m.Type == "started") Send(m); }, partial, window))
            {
                engine.Start();
                reason = Record(engine, capture, window, partial, commands, Send);
            }
            MediaTools.Probe(partial, CancellationToken.None).GetAwaiter().GetResult();
            File.Move(partial, file);
            Send(new("saved", reason.Length == 0 ? "Recording saved." : reason, Path: file));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("Continuous recording: " + ex);
            string recovery = File.Exists(partial) && new FileInfo(partial).Length > 0 ? $" Partial recording: {partial}" : "";
            Send(new("error", ex.Message + recovery));
            return 1;
        }
    }

    private static string Record(Recorder engine, Settings capture, nint window, string path,
        ConcurrentQueue<string> commands, Action<WorkerMessage> send)
    {
        bool paused = false, stop = false;
        var elapsed = Stopwatch.StartNew();
        var check = Stopwatch.StartNew();
        var update = Stopwatch.StartNew();
        string reason = "";
        while (!stop && engine.OutputActive)
        {
            while (commands.TryDequeue(out var command))
            {
                if (command == "stop") { stop = true; break; }
                if (command != "pause") continue;
                if (!engine.PauseOutput(!paused))
                {
                    send(new("notice", "Could not change the recording pause state."));
                    continue;
                }
                paused = !paused;
                if (paused) elapsed.Stop(); else elapsed.Start();
                send(new(paused ? "paused" : "resumed", Seconds: (int)elapsed.Elapsed.TotalSeconds));
            }
            if (stop) break;
            if (capture.Recording.Timed && elapsed.Elapsed.TotalSeconds >= capture.Recording.DurationSeconds)
                reason = "Timed recording saved.";
            if (check.ElapsedMilliseconds >= 500)
            {
                check.Restart();
                try
                {
                    string directory = Path.GetDirectoryName(path)!;
                    if (!Directory.Exists(directory)) reason = "Recording stopped because the save folder is unavailable.";
                    else if (FreeSpace(directory) < Reserve)
                        reason = "Recording saved. The drive is low on space.";
                }
                catch (IOException) { reason = "Recording stopped because the drive is unavailable."; }
                if (window != 0 && !AppTargets.HasWindow(window))
                    reason = "Recording saved. The selected window closed.";
                if (window == 0 && capture.Monitor.Length != 0 && !Screen.AllScreens.Any(s => s.DeviceName == capture.Monitor))
                    reason = "Recording saved. The selected display was disconnected.";
            }
            if (update.ElapsedMilliseconds >= 150)
            {
                update.Restart();
                var file = new FileInfo(path);
                var levels = engine.AudioLevels;
                send(new("progress", Seconds: (int)elapsed.Elapsed.TotalSeconds, Bytes: file.Exists ? file.Length : 0,
                    GameLevel: paused ? 0 : levels.Game, MicLevel: paused ? 0 : levels.Microphone));
            }
            if (reason.Length != 0) break;
            Thread.Sleep(50);
        }
        send(new("finishing", "Saving recording…", (int)elapsed.Elapsed.TotalSeconds));
        if (engine.OutputActive) engine.StopRecording(paused);
        else if (reason.Length == 0) reason = "Recording ended early. " + engine.OutputError;
        return reason;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong capacity, out ulong free);
}
