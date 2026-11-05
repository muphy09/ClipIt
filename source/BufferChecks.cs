using System.Security.Cryptography;
using System.Text.Json;

namespace ClipIt;

internal static class BufferChecks
{
    internal static async Task<int> Run(string root)
    {
        Directory.CreateDirectory(root);
        string folder = Path.Combine(Path.GetFullPath(root), "buffer-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Require(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        try
        {
            var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=8" };
            foreach (int hz in new[] { 440, 440, 880, 1320 }) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={hz}:duration=8"]);
            args.AddRange(["-map", "0:v"]);
            for (int i = 0; i < 4; i++) args.AddRange(["-map", $"{i + 1}:a"]);
            args.AddRange(["-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-c:a", "aac", "-f", "segment", "-segment_time", "2", "-reset_timestamps", "1", "-segment_format", "matroska", Path.Combine(folder, ".clipit-segment-%03d.mkv")]);
            Require((await MediaTools.Run("ffmpeg", args, CancellationToken.None)).Exit == 0, "Generated independent keyframe-aligned disk segments with four audio tracks");
            string[] segments = Directory.GetFiles(folder, "*.mkv").Order().ToArray();
            Require(segments.Length == 4, "Generated four two-second segments");
            string target = Path.Combine(folder, "saved.mp4");
            await DiskReplayBuffer.Export(segments, target, 4, .25, CancellationToken.None);
            var info = await MediaTools.Probe(target, CancellationToken.None);
            Require(info.HasStems && info.Duration >= 4 && info.Duration <= 6.2, "Disk save retains all named audio tracks and a keyframe-aligned replay window");
            Require((await MediaTools.Run("ffmpeg", ["-v", "error", "-xerror", "-i", target, "-map", "0", "-f", "null", "-"], CancellationToken.None)).Exit == 0, "Entire disk-backed clip decodes successfully");
            async Task<List<string>> PacketHashes(string file)
            {
                var result = await MediaTools.Run("ffprobe", ["-v", "error", "-show_packets", "-show_entries", "packet=data_hash", "-show_data_hash", "sha256", "-of", "json", file], CancellationToken.None);
                using var json = JsonDocument.Parse(result.Output);
                return json.RootElement.GetProperty("packets").EnumerateArray().Select(p => p.GetProperty("data_hash").GetString()!).ToList();
            }
            var sourceHashes = new HashSet<string>();
            foreach (string file in segments) sourceHashes.UnionWith(await PacketHashes(file));
            var savedHashes = await PacketHashes(target);
            Require(savedHashes.Count > 100 && savedHashes.All(sourceHashes.Contains), "Every saved video/audio packet is unchanged from the disk segments; no re-encoding");
            var timing = await MediaTools.Run("ffprobe", ["-v", "error", "-show_entries", "stream=start_time", "-of", "json", target], CancellationToken.None);
            using (var json = JsonDocument.Parse(timing.Output))
            {
                var starts = json.RootElement.GetProperty("streams").EnumerateArray().Select(s => double.Parse(s.GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Require(starts.Max() - starts.Min() < .1, "Saved video and all four audio tracks start within 100 ms");
            }
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
            bool rejected = false;
            try { await DiskReplayBuffer.Export(segments, target, 4, 0, CancellationToken.None); } catch (IOException) { rejected = true; }
            Require(rejected && hash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))), "Disk saves never overwrite an existing clip");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            rejected = false;
            try { await DiskReplayBuffer.Export(segments, Path.Combine(folder, "cancelled.mp4"), 4, 0, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
            Require(rejected && !File.Exists(Path.Combine(folder, "cancelled.mp4")) && !Directory.GetFiles(folder, ".clipit-save-*").Any(), "Cancelled saves leave no partial output or published clip");

            var settings = new Settings { Folder = Path.Combine(folder, "cleanup") };
            string bufferRoot = Path.Combine(settings.Folder, ".clipit-buffer");
            using (var active = new DiskReplayBuffer(settings, _ => { }))
            {
                File.WriteAllText(active.InitialPath, "generated buffer fixture");
                using (var second = new DiskReplayBuffer(settings, _ => { }))
                    Require(File.Exists(active.InitialPath), "Concurrent worker startup preserves locked active buffer sessions");
                string abandoned = Path.Combine(bufferRoot, "session-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(abandoned); File.WriteAllText(Path.Combine(abandoned, "session.lock"), "");
                File.WriteAllText(Path.Combine(abandoned, ".clipit-segment-old.mkv"), "fixture");
                using var recovered = new DiskReplayBuffer(settings, _ => { });
                Require(!Directory.Exists(abandoned), "Worker startup removes abandoned generated replay segments");
            }
            Require(!Directory.EnumerateDirectories(bufferRoot).Any(), "Normal shutdown removes generated replay sessions");
            string sentinelFolder;
            using (var buffer = new DiskReplayBuffer(settings, _ => { }))
            {
                sentinelFolder = buffer.DirectoryPath;
                File.WriteAllText(Path.Combine(sentinelFolder, "keep.txt"), "unrelated file");
            }
            Require(File.ReadAllText(Path.Combine(sentinelFolder, "keep.txt")) == "unrelated file", "Cleanup never deletes unrelated files placed in a buffer session");
            File.WriteAllText(Path.Combine(root, "buffer-check-results.json"), JsonSerializer.Serialize(new { Success = true, Folder = folder, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "buffer-check-results.json"), JsonSerializer.Serialize(new { Success = false, Folder = folder, Error = ex.ToString(), Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
