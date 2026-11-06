using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ClipIt;

internal sealed class DiskReplayBuffer : IDisposable
{
    private sealed record Inspection(double Duration, Exception? Error = null);
    private sealed record Segment(string Path, double End)
    {
        internal TaskCompletionSource<Inspection> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Settings settings;
    private readonly Action<WorkerMessage> send;
    private readonly ConcurrentQueue<(string Next, double Time)> changes = new();
    private readonly List<Segment> segments = [];
    private readonly Stopwatch clock = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly FileStream sessionLock;
    // One probe at a time, with a bounded backlog even if the drive stops responding.
    private readonly Channel<Segment> indexing = Channel.CreateBounded<Segment>(160);
    private readonly Task indexWorker;
    private Task<string>? export;
    private string? destination;
    private double requestedAt;
    private string current;
    private DateTime nextSpaceCheck;
    internal string DirectoryPath { get; }
    internal string InitialPath { get; }
    internal bool Saving => destination != null || export != null;

    internal DiskReplayBuffer(Settings settings, Action<WorkerMessage> send)
    {
        this.settings = settings; this.send = send;
        string root = Path.GetFullPath(Path.Combine(settings.Folder, ".clipit-buffer"));
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("The replay buffer folder must not be a link or junction.");
        foreach (string abandoned in Directory.EnumerateDirectories(root, "session-*")) TryClean(abandoned, root);
        DirectoryPath = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        sessionLock = new FileStream(Path.Combine(DirectoryPath, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        InitialPath = current = Path.Combine(DirectoryPath, ".clipit-segment-initial.mp4");
        CheckSpace();
        indexWorker = Task.Run(IndexSegments);
    }

    internal void StartClock() => clock.Restart();
    internal void FileChanged(string next) => changes.Enqueue((next, clock.Elapsed.TotalSeconds));
    internal void Save(string target)
    {
        if (Saving) throw new IOException("A clip is already saving.");
        destination = target; requestedAt = clock.Elapsed.TotalSeconds;
    }

    internal void Tick()
    {
        while (changes.TryDequeue(out var change))
        {
            string next = Path.GetFullPath(change.Next);
            if (!next.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected replay segment path.");
            var closed = new Segment(current, change.Time);
            segments.Add(closed); current = next;
            if (!indexing.Writer.TryWrite(closed)) throw new IOException("Replay segment inspection cannot keep up with recording.");
            if (destination is { } target && change.Time >= requestedAt)
            {
                destination = null;
                double afterRequest = Math.Max(0, change.Time - requestedAt);
                var snapshot = segments.ToArray();
                Log.Write($"Disk replay split after {clock.Elapsed.TotalSeconds - requestedAt:0.000}s; {snapshot.Count(s => s.Ready.Task.IsCompletedSuccessfully)}/{snapshot.Length} segments indexed.");
                export = ExportIndexed(snapshot, target, afterRequest, lifetime.Token);
            }
        }
        if (export?.IsCompleted == true)
        {
            try
            {
                string path = export.GetAwaiter().GetResult();
                Log.Write($"Disk replay published and notifying after {clock.Elapsed.TotalSeconds - requestedAt:0.000}s.");
                send(new("saved", "Replay saved", Path: path));
            }
            catch (Exception ex) { Log.Write("Disk replay save: " + ex); send(new("error", "Could not save replay: " + ex.Message)); }
            finally { export = null; }
        }
        foreach (var segment in segments)
            if (segment.Ready.Task.IsCompletedSuccessfully && segment.Ready.Task.Result.Error is { } error)
                throw new IOException("Could not inspect a finalized replay segment.", error);
        if (!Saving)
            while (segments.Count > 2 && segments[0].Ready.Task.IsCompleted && segments[0].End < clock.Elapsed.TotalSeconds - settings.ReplaySeconds - 4)
            {
                File.Delete(segments[0].Path); segments.RemoveAt(0);
            }
        if (DateTime.UtcNow >= nextSpaceCheck) { CheckSpace(); nextSpaceCheck = DateTime.UtcNow.AddSeconds(5); }
    }

    private async Task IndexSegments()
    {
        try
        {
            await foreach (var segment in indexing.Reader.ReadAllAsync(lifetime.Token))
            {
                try { segment.Ready.SetResult(new(await FinalizedDuration(segment.Path, lifetime.Token))); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception ex) { segment.Ready.SetResult(new(0, ex)); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task<string> ExportIndexed(Segment[] snapshot, string target, double afterRequest, CancellationToken token)
    {
        var durations = new double[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            var inspected = await snapshot[i].Ready.Task.WaitAsync(token);
            if (inspected.Error != null) throw new IOException("Could not inspect a replay segment.", inspected.Error);
            durations[i] = inspected.Duration;
        }
        Log.Write($"Disk replay segments ready after {clock.Elapsed.TotalSeconds - requestedAt:0.000}s.");
        return await Export(snapshot.Select(s => s.Path).ToArray(), durations, target, settings.ReplaySeconds, afterRequest, token);
    }

    private void CheckSpace()
    {
        if (new DriveInfo(BufferDrive.VolumeRoot(DirectoryPath)).AvailableFreeSpace < 128_000_000)
            throw new IOException("The disk replay buffer is low on free space. Free space, choose another save drive, or switch to RAM buffering.");
    }

    internal static async Task<string> Export(string[] segments, string target, int seconds, double afterRequest, CancellationToken token)
    {
        var durations = new double[segments.Length];
        for (int i = 0; i < segments.Length; i++) durations[i] = await FinalizedDuration(segments[i], token);
        return await Export(segments, durations, target, seconds, afterRequest, token);
    }

    internal static async Task<string> Export(string[] segments, double[] durations, string target, int seconds, double afterRequest, CancellationToken token)
    {
        if (segments.Length == 0) throw new IOException("The replay buffer is not ready yet.");
        if (durations.Length != segments.Length || durations.Any(d => !double.IsFinite(d) || d <= 0)) throw new IOException("Invalid replay segment timing.");
        var timing = Stopwatch.StartNew();
        string list = Path.Combine(Path.GetDirectoryName(segments[0])!, ".clipit-" + Guid.NewGuid().ToString("N") + ".ffconcat");
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".clipit-save-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            double total = durations.Sum();
            double end = Math.Max(.1, total - afterRequest);
            double start = Math.Max(0, end - seconds);
            // Start on an existing keyframe, at most one short segment before the requested start.
            // Explicit video durations prevent container padding accumulating between segments.
            int first = 0; double skipped = 0;
            while (first + 1 < segments.Length && skipped + durations[first] <= start) skipped += durations[first++];
            var manifest = new StringBuilder("ffconcat version 1.0\n");
            for (int i = first; i < segments.Length; i++)
            {
                manifest.Append("file '").Append(segments[i].Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
                manifest.Append("duration ").Append(durations[i].ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
            }
            await File.WriteAllTextAsync(list, manifest.ToString(), new UTF8Encoding(false), token);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-auto_convert", "0", "-i", list,
                "-t", (end - skipped).ToString("0.######", CultureInfo.InvariantCulture), "-map", "0:v:0", "-map", "0:a", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart" };
            string[] names = ["ClipIt Mix", "Game", "Discord", "Microphone"];
            for (int i = 0; i < names.Length; i++) args.AddRange([$"-metadata:s:a:{i}", "handler_name=" + names[i], $"-metadata:s:a:{i}", "title=" + names[i]]);
            args.AddRange(["-n", temporary]);
            var result = await MediaTools.Run("ffmpeg", args, token, 80);
            if (result.Exit != 0) throw new IOException("Could not assemble the replay. " + result.Error);
            Log.Write($"Disk replay assembly completed in {timing.Elapsed.TotalSeconds:0.000}s.");
            timing.Restart();
            var saved = await MediaTools.Probe(temporary, token);
            if (!saved.HasStems) throw new IOException("The replay did not retain all four audio tracks.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
            Log.Write($"Disk replay verification and publication completed in {timing.Elapsed.TotalSeconds:0.000}s.");
            return target;
        }
        finally
        {
            try { File.Delete(list); } catch { }
            try { File.Delete(temporary); } catch { }
        }
    }

    private static async Task<double> FinalizedDuration(string file, CancellationToken token)
    {
        // OBS signals a split before its helper necessarily finishes the preceding MP4.
        for (int attempt = 0; ; attempt++)
        {
            try { return await SegmentDuration(file, token); }
            catch (IOException) when (attempt < 29) { await Task.Delay(100, token); }
        }
    }

    private static async Task<double> SegmentDuration(string path, CancellationToken token)
    {
        var result = await MediaTools.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,duration_time", "-of", "json", path], token, 15);
        if (result.Exit != 0 || result.Error.Length > 0) throw new IOException("A replay segment is not finalized yet.");
        using var json = JsonDocument.Parse(result.Output);
        double first = double.MaxValue, end = 0;
        foreach (var packet in json.RootElement.GetProperty("packets").EnumerateArray())
        {
            if (!packet.TryGetProperty("pts_time", out var pts) || !double.TryParse(pts.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double position)) continue;
            double duration = packet.TryGetProperty("duration_time", out var value) && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 1.0 / 60;
            first = Math.Min(first, position); end = Math.Max(end, position + duration);
        }
        if (first == double.MaxValue || end <= first) throw new IOException("A replay segment contains no video frames.");
        return end - first;
    }

    private static void TryClean(string directory, string root)
    {
        // Only delete generated files in a direct session child; never recurse through links or unknown user files.
        try
        {
            string full = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            string lockFile = Path.Combine(full, "session.lock");
            if (!File.Exists(lockFile)) return;
            using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                foreach (string file in Directory.EnumerateFiles(full, ".clipit-*"))
                    if (Path.GetExtension(file) is ".mp4" or ".mkv" or ".ffconcat") File.Delete(file);
            File.Delete(lockFile);
            if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        catch (IOException) { } // An active worker or export still owns this session.
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        indexing.Writer.TryComplete();
        try { indexWorker.GetAwaiter().GetResult(); } catch { }
        try { export?.GetAwaiter().GetResult(); } catch { }
        lifetime.Dispose(); sessionLock.Dispose();
        TryClean(DirectoryPath, Path.GetDirectoryName(DirectoryPath)!);
    }
}
