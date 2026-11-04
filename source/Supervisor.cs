using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClipIt;

internal sealed class Supervisor : IDisposable
{
    private Process? process;
    private NamedPipeServerStream? pipe;
    private StreamWriter? writer;
    private int generation;
    private DateTime lastMessage, nextStart;
    private int failures;
    private readonly ConcurrentQueue<(int Generation, WorkerMessage Message)> messages = new();
    public bool Enabled { get; private set; } = true;
    public bool Recording { get; private set; }
    public bool Saving { get; private set; }
    public int Seconds { get; private set; }
    public string Status { get; private set; } = "Starting recorder…";
    public event Action<WorkerMessage>? Message;
    public Settings Settings { get; set; }
    public Supervisor(Settings settings) { Settings = settings; }

    public void Tick()
    {
        while (messages.TryDequeue(out var item))
        {
            if (item.Generation != generation) continue;
            var message = item.Message;
            lastMessage = DateTime.UtcNow;
            switch (message.Type)
            {
                case "started": Recording = true; Status = "Buffer warming up…"; break;
                case "heartbeat":
                    Recording = true; Seconds = message.Seconds;
                    Status = Seconds >= 120 ? "Ready · last 2 minutes available" : $"Recording · {Seconds}s of 120s buffered";
                    if (Seconds >= 60) failures = 0;
                    break;
                case "saving": Saving = true; break;
                case "saved": case "error": Saving = false; break;
                case "fatal": Recover(message.Text); break;
            }
            Message?.Invoke(message);
        }
        if (!Enabled) return;
        if (process != null && (process.HasExited || DateTime.UtcNow - lastMessage > TimeSpan.FromSeconds(35)))
            Recover(process.HasExited ? "Recorder exited unexpectedly." : "Recorder stopped responding.");
        if (process == null && DateTime.UtcNow >= nextStart) Start();
    }

    private void Start()
    {
        int current = ++generation;
        try
        {
            string name = "ClipIt-" + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var server = pipe;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--worker"); start.ArgumentList.Add(name);
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Settings))));
            process = Process.Start(start) ?? throw new IOException("Could not start recorder.");
            lastMessage = DateTime.UtcNow; Status = "Starting recorder…";
            Log.Write($"Started recording worker {process.Id}");
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await server.WaitForConnectionAsync(timeout.Token);
                    var connectedWriter = new StreamWriter(server, new UTF8Encoding(false), leaveOpen:true) { AutoFlush = true };
                    if (current != generation) { connectedWriter.Dispose(); return; }
                    writer = connectedWriter;
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen:true);
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (JsonSerializer.Deserialize<WorkerMessage>(line) is { } m) messages.Enqueue((current, m));
                    }
                }
                catch (Exception ex) { Log.Write("Recorder connection: " + ex.Message); }
            });
        }
        catch (Exception ex) { Recover(ex.Message); }
    }

    private void Recover(string reason)
    {
        StopWorker();
        failures++;
        int delay = Math.Min(60, 2 << Math.Min(failures - 1, 5));
        Status = $"Recovering in {delay}s · {reason}";
        nextStart = DateTime.UtcNow.AddSeconds(delay);
        Log.Write(Status);
        Message?.Invoke(new("recovering", Status));
    }
    public void Save()
    {
        if (!Enabled || !Recording) { Message?.Invoke(new("notice", "Recording is not ready yet.")); return; }
        if (Saving) { Message?.Invoke(new("notice", "Your previous clip is still saving.")); return; }
        Send("save");
    }
    public void RefreshMicrophones() => Send("devices");
    private void Send(string command)
    {
        try { var w = writer; if (w != null) lock (w) w.WriteLine(command); }
        catch (Exception ex) { Recover("Recorder connection failed: " + ex.Message); }
    }
    public void Restart()
    {
        if (!Enabled) return;
        StopWorker(); nextStart = DateTime.UtcNow; Status = "Restarting recorder…";
    }
    public void Toggle()
    {
        Enabled = !Enabled;
        if (!Enabled) { StopWorker(); Status = "Paused · replay recording is off"; }
        else { nextStart = DateTime.UtcNow; Status = "Starting recorder…"; }
    }
    private void StopWorker()
    {
        ++generation;
        var oldProcess = process; process = null;
        var oldWriter = writer; writer = null;
        try { if (oldWriter != null) lock (oldWriter) oldWriter.WriteLine("stop"); } catch { }
        var oldPipe = pipe; pipe = null;
        Recording = false; Saving = false; Seconds = 0;
        _ = Task.Run(() =>
        {
            try { if (oldProcess != null && !oldProcess.WaitForExit(6000)) oldProcess.Kill(true); }
            catch { }
            finally
            {
                oldProcess?.Dispose();
                try { oldWriter?.Dispose(); } catch { }
                try { oldPipe?.Dispose(); } catch { }
            }
        });
    }
    public void Dispose() { Enabled = false; StopWorker(); }
}
