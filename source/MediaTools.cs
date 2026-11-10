using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipIt;

public sealed record AudioDevice(string Id, string Name) { public override string ToString() => Name; }
public sealed record AudioTrackInfo(int Index, string Name);
public sealed record MixSettings(double Game = 1, double Discord = 1, double Microphone = 1, bool GameMuted = false, bool DiscordMuted = false, bool MicrophoneMuted = false)
{
    public double GameGain => GameMuted ? 0 : Safe(Game);
    public double DiscordGain => DiscordMuted ? 0 : Safe(Discord);
    public double MicrophoneGain => MicrophoneMuted ? 0 : Safe(Microphone);
    private static double Safe(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
}
public sealed record ClipInfo(double Duration, int Width, int Height, int AudioTracks, long Size)
{
    public List<AudioTrackInfo> Tracks { get; init; } = [];
    public MixSettings Mix { get; init; } = new();
    public bool HasStems => Tracks.Count == 4 && Tracks[0].Name.Contains("ClipIt", StringComparison.OrdinalIgnoreCase);
}
public readonly record struct ClipStamp(long Size, DateTime Modified)
{
    public static ClipStamp Read(string path) { var f = new FileInfo(path); return new(f.Length, f.LastWriteTimeUtc); }
}

internal sealed record ClipPreviewInfo(double Duration, int Width, int Height);

internal static class MediaTools
{
    public static string Gigabytes(long bytes)
    {
        double size = bytes / 1048576000.0;
        return size is > 0 and < 0.01 ? "<0.01 GB" : $"{size:0.00} GB";
    }

    private static readonly SemaphoreSlim ThumbnailSlots = new(1);
    private static readonly SemaphoreSlim MediaJobs = new(2);
    private static string Tool(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException("ClipIt's media tools are missing. Keep the complete app folder together.", path);
        return path;
    }
    internal static async Task<(int Exit, string Output, string Error)> Run(string tool, IEnumerable<string> arguments, CancellationToken token, int seconds = 120)
    {
        await MediaJobs.WaitAsync(token);
        try { return await RunCore(tool, arguments, token, seconds); }
        finally { MediaJobs.Release(); }
    }
    private static async Task<(int Exit, string Output, string Error)> RunCore(string tool, IEnumerable<string> arguments, CancellationToken token, int seconds)
    {
        var start = new ProcessStartInfo(Tool(tool)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        process.Start();
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            if (token.IsCancellationRequested) throw;
            throw new IOException("The media operation took too long. The original clip has been kept.");
        }
        return (process.ExitCode, await output, await error);
    }
    public static async Task<ClipInfo> Probe(string path, CancellationToken token)
    {
        var result = await Run("ffprobe", ["-v", "error", "-show_entries", "format=duration,size:format_tags=comment:stream=codec_type,width,height,duration:stream_tags=name,title,handler_name", "-of", "json", path], token, 20);
        if (result.Exit != 0) throw new IOException("This clip is still being saved, is unavailable, or could not be read.");
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        double duration = root.GetProperty("format").TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        int width = 0, height = 0, tracks = 0;
        var audio = new List<AudioTrackInfo>();
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            string type = stream.GetProperty("codec_type").GetString() ?? "";
            if (type == "video" && width == 0) { width = stream.GetProperty("width").GetInt32(); height = stream.GetProperty("height").GetInt32(); }
            if (type == "audio")
            {
                string name = "Audio " + (tracks + 1);
                if (stream.TryGetProperty("tags", out var tags))
                    foreach (string key in new[] { "name", "title", "handler_name" })
                        if (tags.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString())) { name = value.GetString()!; break; }
                audio.Add(new(tracks++, name));
            }
        }
        if (duration <= 0 || width == 0) throw new IOException("No playable video was found in this clip.");
        MixSettings mix = new();
        if (root.GetProperty("format").TryGetProperty("tags", out var global) && global.TryGetProperty("comment", out var comment) && comment.GetString() is { } text && text.StartsWith("ClipItMix:"))
            try { mix = JsonSerializer.Deserialize<MixSettings>(text[10..]) ?? new(); } catch { }
        return new(duration, width, height, tracks, new FileInfo(path).Length) { Tracks = audio, Mix = mix };
    }

    public static async Task<string> Thumbnail(string path, ClipStamp stamp, CancellationToken token)
    {
        string cache = Path.Combine(Settings.DataDirectory, "thumbnails"); Directory.CreateDirectory(cache);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + stamp)));
        string target = Path.Combine(cache, key + ".jpg");
        if (File.Exists(target)) return target;
        await ThumbnailSlots.WaitAsync(token);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".jpg";
        try
        {
            if (File.Exists(target)) return target;
            var result = await Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-threads", "1", "-ss", "0", "-i", path, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "5", "-update", "1", "-y", temporary], token, 15);
            if (result.Exit != 0 || !File.Exists(temporary)) throw new IOException("Thumbnail unavailable.");
            File.Move(temporary, target, true);
            return target;
        }
        finally { TryDelete(temporary); ThumbnailSlots.Release(); }
    }

    public static async Task<ClipPreviewInfo> PreviewInfo(string path, string thumbnail, CancellationToken token)
    {
        string target = thumbnail + ".json";
        try
        {
            if (File.Exists(target) && JsonSerializer.Deserialize<ClipPreviewInfo>(await File.ReadAllTextAsync(target, token)) is { } cached)
                return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        var info = await Probe(path, token);
        var preview = new ClipPreviewInfo(info.Duration, info.Width, info.Height);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preview), token);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { TryDelete(temporary); }
        return preview;
    }

    public static async Task<AudioPreview> ExtractStems(string file, CancellationToken token)
    {
        string directory = Path.Combine(Settings.DataDirectory, "preview-audio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string[] files = Enumerable.Range(0, 3).Select(i => Path.Combine(directory, $"stem-{i}.wav")).ToArray();
        var preview = new AudioPreview(directory, files);
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", file };
            for (int i = 0; i < 3; i++) args.AddRange(["-map", $"0:a:{i + 1}", "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-y", files[i]]);
            var result = await Run("ffmpeg", args, token, 120);
            if (result.Exit != 0) throw new IOException("Separate preview audio could not be prepared.");
            return preview;
        }
        catch { preview.Dispose(); throw; }
    }

    public static async Task<ClipInfo> Trim(string original, string destination, double start, double end, ClipStamp expected, CancellationToken token, MixSettings? mix = null)
    {
        if (start < 0 || end - start < 0.1 || !double.IsFinite(end)) throw new ArgumentException("Choose a trim range of at least 0.1 seconds.");
        if (ClipStamp.Read(original) != expected) throw new IOException("This clip changed since it was opened. Reopen it before trimming.");
        bool overwrite = string.Equals(Path.GetFullPath(original), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
        if (!overwrite && File.Exists(destination)) throw new IOException("A file with that name already exists. Choose a new export filename.");
        string directory = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".clipit-render-" + Guid.NewGuid().ToString("N") + ".mp4");
        double duration = end - start;
        string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        try
        {
            // Precise boundaries need re-encoding; stream-copy trimming can start at an earlier keyframe.
            var common = new List<string> { "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin", "-ss", F(start), "-i", original, "-t", F(duration) };
            if (mix != null && (await Probe(original, token)).HasStems)
            {
                string filter = $"[0:a:1]asplit=2[graw][gm];[0:a:2]asplit=2[draw][dm];[0:a:3]asplit=2[mraw][mm];[gm]volume={F(mix.GameGain)}[g];[dm]volume={F(mix.DiscordGain)}[d];[mm]volume={F(mix.MicrophoneGain)}[m];[g][d][m]amix=inputs=3:normalize=0,alimiter=limit=0.98:level=0:latency=1[out]";
                common.AddRange(["-filter_complex", filter, "-map", "0:v:0", "-map", "[out]", "-map", "[graw]", "-map", "[draw]", "-map", "[mraw]"]);
                string[] names = ["ClipIt Mix", "Game", "Discord", "Microphone"];
                for (int i = 0; i < names.Length; i++) common.AddRange([$"-metadata:s:a:{i}", "handler_name=" + names[i], $"-metadata:s:a:{i}", "name=" + names[i]]);
            }
            else
            {
                common.AddRange(["-map", "0:v:0", "-map", "0:a?"]);
                if (mix != null) common.AddRange(["-af", "volume=" + F(mix.GameGain)]);
            }
            common.AddRange(["-map_metadata", "0"]);
            if (mix != null) common.AddRange(["-metadata", "comment=ClipItMix:" + JsonSerializer.Serialize(mix)]);
            string[] tail = ["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-metadata", "encoder=ClipIt", "-y", temp];
            var result = await Run("ffmpeg", common.Concat(["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "20", "-b:v", "0"]).Concat(tail), token, 900);
            if (result.Exit != 0)
            {
                token.ThrowIfCancellationRequested();
                Log.Write("Hardware trim unavailable; using two-thread software export: " + result.Error);
                result = await Run("ffmpeg", common.Concat(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-threads", "2"]).Concat(tail), token, 1800);
            }
            if (result.Exit != 0) { Log.Write("Trim failed: " + result.Error); throw new IOException("The clip could not be rendered. Your original has been kept."); }
            var info = await Probe(temp, token);
            if (Math.Abs(info.Duration - duration) > Math.Max(0.35, duration * .01)) throw new IOException("The rendered duration did not match the selection. The original has been kept.");
            token.ThrowIfCancellationRequested();
            if (ClipStamp.Read(original) != expected) throw new IOException("The original changed during rendering. It has not been replaced.");
            if (overwrite)
            {
                // Same-volume replacement is atomic: never delete the original before a valid render exists.
                File.Replace(temp, original, null);
            }
            else File.Move(temp, destination, false);
            return info;
        }
        finally { TryDelete(temp); }
    }
    private static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
    public static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero));
        return t.TotalHours >= 1 ? $@"{(long)t.TotalHours}:{t:mm\:ss}" : t.ToString(@"m\:ss");
    }
}

internal sealed record AudioPreview(string DirectoryPath, string[] Files) : IDisposable
{
    public void Dispose()
    {
        void Clean() { foreach (string file in Files) try { File.Delete(file); } catch { } try { Directory.Delete(DirectoryPath, false); } catch { } }
        Clean(); _ = Task.Run(async () => { await Task.Delay(800); Clean(); });
    }
}
